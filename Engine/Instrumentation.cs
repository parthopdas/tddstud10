﻿using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using R4nd0mApps.TddStud10.Common;
using R4nd0mApps.TddStud10.Common.Domain;
using R4nd0mApps.TddStud10.Engine.Diagnostics;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace R4nd0mApps.TddStud10
{
    internal static class Instrumentation
    {
        public static PerDocumentSequencePoints GenerateSequencePointInfo(IRunExecutorHost host, RunStartParams rsp)
        {
            try
            {
                return GenerateSequencePointInfoImpl(host, rsp);
            }
            catch (Exception e)
            {
                Logger.I.LogError("Failed to instrument. Exception: {0}", e);
            }

            return null;
        }

        private static PerDocumentSequencePoints GenerateSequencePointInfoImpl(IRunExecutorHost host, RunStartParams rsp)
        {
            var timeFilter = rsp.StartTime;
            var buildOutputRoot = rsp.Solution.BuildRoot.Item;
            Logger.I.LogInfo(
                "Generating sequence point info: Time filter - {0}, Build output root - {1}.",
                timeFilter.ToLocalTime(),
                buildOutputRoot);

            var perDocSP = new PerDocumentSequencePoints();
            Engine.Engine.FindAndExecuteForEachAssembly(
                host,
                buildOutputRoot,
                timeFilter,
                (string assemblyPath) =>
                {
                    Logger.I.LogInfo("Generating sequence point info for {0}.", assemblyPath);

                    var assembly = AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters { ReadSymbols = true });

                    VisitAllTypes(
                        assembly.Modules,
                        (m, t) =>
                        {
                            FindSequencePointForType(rsp, perDocSP, m, t);
                        });
                });

            return perDocSP;
        }

        private static void FindSequencePointForType(RunStartParams rsp, PerDocumentSequencePoints perDocSP, ModuleDefinition module, TypeDefinition type)
        {
            foreach (MethodDefinition meth in type.Methods)
            {
                if (IsMethodSkipped(meth))
                {
                    continue;
                }

                var sps = from i in meth.Body.Instructions
                          where i.SequencePoint != null
                          where i.SequencePoint.StartLine != 0xfeefee
                          select new { module, meth, i.SequencePoint };

                int id = 0;
                foreach (var sp in sps)
                {
                    var fp = PathBuilder.rebaseCodeFilePath(rsp.Solution.Path, rsp.Solution.SnapshotPath, FilePath.NewFilePath(sp.SequencePoint.Document.Url));
                    var seqPts = perDocSP.GetOrAdd(fp, _ => new ConcurrentBag<R4nd0mApps.TddStud10.Common.Domain.SequencePoint>());

                    seqPts.Add(new R4nd0mApps.TddStud10.Common.Domain.SequencePoint
                    {
                        id = new SequencePointId
                        {
                            methodId = new MethodId(AssemblyId.NewAssemblyId(sp.module.Mvid), MdTokenRid.NewMdTokenRid(sp.meth.MetadataToken.RID)),
                            uid = id++
                        },
                        document = fp,
                        startLine = DocumentCoordinate.NewDocumentCoordinate(sp.SequencePoint.StartLine),
                        startColumn = DocumentCoordinate.NewDocumentCoordinate(sp.SequencePoint.StartColumn),
                        endLine = DocumentCoordinate.NewDocumentCoordinate(sp.SequencePoint.EndLine),
                        endColumn = DocumentCoordinate.NewDocumentCoordinate(sp.SequencePoint.EndColumn),
                    });
                }
            }
        }

        public static void Instrument(IRunExecutorHost host, RunStartParams rsp, Func<DocumentLocation, IEnumerable<DTestCase>> findTest)
        {
            try
            {
                InstrumentImpl(host, rsp, findTest);
            }
            catch (Exception e)
            {
                Logger.I.LogError("Failed to instrument. Exception: {0}", e);
            }
        }

        private static void VisitAllTypes(IEnumerable<ModuleDefinition> modules, Action<ModuleDefinition, TypeDefinition> action)
        {
            foreach (var module in modules)
            {
                VisitAllTypes(module.Types, module, action);
            }
        }

        private static void VisitAllTypes(IEnumerable<TypeDefinition> types, ModuleDefinition module, Action<ModuleDefinition, TypeDefinition> action)
        {
            foreach (var type in types)
            {
                action(module, type);
                VisitAllTypes(type.NestedTypes, module, action);
            }
        }

        private static void InstrumentImpl(IRunExecutorHost host, RunStartParams rsp, Func<DocumentLocation, IEnumerable<DTestCase>> findTest)
        {
            var timeFilter = rsp.StartTime;
            var solutionSnapshotRoot = Path.GetDirectoryName(rsp.Solution.SnapshotPath.Item);
            var solutionRoot = Path.GetDirectoryName(rsp.Solution.Path.Item);
            var buildOutputRoot = rsp.Solution.BuildRoot.Item;
            Logger.I.LogInfo(
                "Instrumenting: Time filter - {0}, Build output root - {1}.",
                timeFilter.ToLocalTime(),
                buildOutputRoot);

            System.Reflection.StrongNameKeyPair snKeyPair = null;
            var snKeyFile = Directory.EnumerateFiles(solutionRoot, "*.snk").FirstOrDefault();
            if (snKeyFile != null)
            {
                snKeyPair = new System.Reflection.StrongNameKeyPair(File.ReadAllBytes(snKeyFile));
                Logger.I.LogInfo("Using strong name from {0}.", snKeyFile);
            }

            var asmResolver = new DefaultAssemblyResolver();
            Array.ForEach(asmResolver.GetSearchDirectories(), asmResolver.RemoveSearchDirectory);
            asmResolver.AddSearchDirectory(buildOutputRoot);
            var readerParams = new ReaderParameters
            {
                AssemblyResolver = asmResolver,
                ReadSymbols = true,
            };

            string testRunnerPath = Path.GetFullPath(typeof(R4nd0mApps.TddStud10.TestRuntime.Marker).Assembly.Location);
            var enterSPMD = from t in ModuleDefinition.ReadModule(testRunnerPath).GetTypes()
                            where t.Name == "Marker"
                            from m in t.Methods
                            where m.Name == "EnterSequencePoint"
                            select m;

            var exitUTMD = from t in ModuleDefinition.ReadModule(testRunnerPath).GetTypes()
                           where t.Name == "Marker"
                           from m in t.Methods
                           where m.Name == "ExitUnitTest"
                           select m;

            Func<string, string> rebaseDocument = s => PathBuilder.rebaseCodeFilePath(rsp.Solution.Path, rsp.Solution.SnapshotPath, FilePath.NewFilePath(s)).Item;

            Engine.Engine.FindAndExecuteForEachAssembly(
                host,
                buildOutputRoot,
                timeFilter,
                (string assemblyPath) =>
                {
                    Logger.I.LogInfo("Instrumenting {0}.", assemblyPath);

                    var assembly = AssemblyDefinition.ReadAssembly(assemblyPath, readerParams);

                    /*
                       IL_0001: ldstr <assemblyId>
                       IL_0006: ldstr <mdtoken>
                       IL_000b: ldstr <spid>
                       IL_000d: call void R4nd0mApps.TddStud10.TestHost.Marker::ExitUnitTest(string, ldstr, ldstr)
                     */
                    MethodReference enterSPMR = assembly.MainModule.Import(enterSPMD.First());
                    MethodReference exitUTMR = assembly.MainModule.Import(exitUTMD.First());

                    VisitAllTypes(
                        assembly.Modules,
                        (m, t) =>
                        {
                            InstrumentType(rsp, findTest, assemblyPath, rebaseDocument, enterSPMR, exitUTMR, m, t);
                        });

                    var backupAssemblyPath = Path.ChangeExtension(assemblyPath, ".original");
                    File.Delete(backupAssemblyPath);
                    File.Move(assemblyPath, backupAssemblyPath);
                    try
                    {
                        assembly.Write(assemblyPath, new WriterParameters { WriteSymbols = true, StrongNameKeyPair = snKeyPair });
                    }
                    catch
                    {
                        Logger.I.LogInfo("Backing up or instrumentation failed. Attempting to revert back changes to {0}.", assemblyPath);
                        File.Delete(assemblyPath);
                        File.Move(backupAssemblyPath, assemblyPath);
                        throw;
                    }

                },
                1);
        }

        private static void InstrumentType(RunStartParams rsp, Func<DocumentLocation, IEnumerable<DTestCase>> findTest, string assemblyPath, Func<string, string> rebaseDocument, MethodReference enterSPMR, MethodReference exitUTMR, ModuleDefinition module, TypeDefinition type)
        {
            foreach (MethodDefinition meth in type.Methods)
            {
                if (IsMethodSkipped(meth))
                {
                    continue;
                }

                meth.Body.SimplifyMacros();

                var spi = from i in meth.Body.Instructions
                          where i.SequencePoint != null
                          where i.SequencePoint.StartLine != 0xfeefee
                          select i;

                var spId = 0;
                var instructions = spi.ToArray();
                foreach (var sp in instructions)
                {
                    if (sp.Previous != null &&
                        (sp.Previous.OpCode.Code == Code.Leave_S
                        || sp.Previous.OpCode.Code == Code.Leave
                        || sp.Previous.OpCode.Code == Code.Endfilter
                        || sp.Previous.OpCode.Code == Code.Endfinally))
                    {
                        continue;
                    }

                    /**********************************************************************************/
                    /*                                PDB Path Replace                                */
                    /**********************************************************************************/
                    sp.SequencePoint.Document.Url = rebaseDocument(sp.SequencePoint.Document.Url);

                    /**********************************************************************************/
                    /*                            Inject Enter Sequence Point                         */
                    /**********************************************************************************/
                    Instruction instrMarker = sp;
                    Instruction instr = null;
                    var ilProcessor = meth.Body.GetILProcessor();

                    // IL_000d: call void R4nd0mApps.TddStud10.TestHost.Marker::EnterSequencePoint(string, ldstr, ldstr)
                    instr = ilProcessor.Create(OpCodes.Call, enterSPMR);
                    ilProcessor.InsertBefore(instrMarker, instr);
                    instrMarker = instr;
                    // IL_000b: ldstr <spid>
                    instr = ilProcessor.Create(OpCodes.Ldstr, (spId++).ToString());
                    ilProcessor.InsertBefore(instrMarker, instr);
                    instrMarker = instr;
                    // IL_0006: ldstr <mdtoken>
                    instr = ilProcessor.Create(OpCodes.Ldstr, meth.MetadataToken.RID.ToString());
                    ilProcessor.InsertBefore(instrMarker, instr);
                    instrMarker = instr;
                    // IL_0001: ldstr <assemblyId>
                    instr = ilProcessor.Create(OpCodes.Ldstr, module.Mvid.ToString());
                    ilProcessor.InsertBefore(instrMarker, instr);
                    instrMarker = instr;
                }

                /*************************************************************************************/
                /*                            Inject Exit Unit Test                                  */
                /*************************************************************************************/
                var ret = IsSequencePointAtStartOfAUnitTest(rsp, spi.Select(i => i.SequencePoint).FirstOrDefault(), FilePath.NewFilePath(assemblyPath), findTest);
                if (ret.Item1)
                {
                    // NOTE: Reeeealy need to bring this class under test. Commenting out the void check as Property tests can return boolean.
                    if (!meth.IsConstructor /*&& meth.ReturnType == module.TypeSystem.Void*/ && !meth.IsAsync())
                    {
                        InjectExitUtCallInsideMethodWiseFinally(module, meth, ret.Item2, exitUTMR);
                    }
                    else
                    {
                        Logger.I.LogError("Instrumentation: Unsupported method type: IsConstructo = {0}, Return Type = {1}, IsAsync = {2}.", meth.IsConstructor, meth.ReturnType, meth.IsAsync());
                    }
                }

                meth.Body.InitLocals = true;
                meth.Body.OptimizeMacros();
            }
        }

        private static bool IsMethodSkipped(MethodDefinition meth)
        {
            return meth.Body == null || meth.Body.Instructions.Count <= 0;
        }

        private static void InjectExitUtCallInsideMethodWiseFinally(
           ModuleDefinition mod,
           MethodDefinition meth, TestId testId, MethodReference exitMarkerMethodRef)
        {
            ILProcessor ilProcessor = meth.Body.GetILProcessor();

            var firstInstruction = FindFirstInstructionSkipCtor(meth);
            Instruction returnInstruction = FixReturns(meth, mod);

            var beforeReturn = Instruction.Create(OpCodes.Endfinally);
            ilProcessor.InsertBefore(returnInstruction, beforeReturn);

            /////////////// Start of try block  
            Instruction nopInstruction1 = Instruction.Create(OpCodes.Nop);
            ilProcessor.InsertBefore(firstInstruction, nopInstruction1);

            //////// Start Finally block
            Instruction nopInstruction2 = Instruction.Create(OpCodes.Nop);
            ilProcessor.InsertBefore(beforeReturn, nopInstruction2);

            Instruction instrMarker = nopInstruction2;
            Instruction instr = null;

            // IL_000d: call void R4nd0mApps.TddStud10.TestHost.Marker::EnterUnitTest(ldstr)
            instr = ilProcessor.Create(OpCodes.Call, exitMarkerMethodRef);
            ilProcessor.InsertBefore(instrMarker, instr);
            instrMarker = instr;
            // IL_0006: ldstr <string>
            instr = ilProcessor.Create(OpCodes.Ldstr, testId.location.line.Item.ToString(CultureInfo.InvariantCulture));
            ilProcessor.InsertBefore(instrMarker, instr);
            instrMarker = instr;
            // IL_0006: ldstr <string>
            instr = ilProcessor.Create(OpCodes.Ldstr, testId.location.document.Item);
            ilProcessor.InsertBefore(instrMarker, instr);
            instrMarker = instr;
            // IL_0006: ldstr <string>
            instr = ilProcessor.Create(OpCodes.Ldstr, testId.source.Item);
            ilProcessor.InsertBefore(instrMarker, instr);
            instrMarker = instr;
            ///////// End finally block

            var handler = new ExceptionHandler(ExceptionHandlerType.Finally)
            {
                TryStart = nopInstruction1,
                TryEnd = instrMarker,
                HandlerStart = instrMarker,
                HandlerEnd = returnInstruction,
            };

            meth.Body.ExceptionHandlers.Add(handler);
        }

        private static Instruction FindFirstInstructionSkipCtor(MethodDefinition med)
        {
            MethodBody body = med.Body;
            if (med.IsConstructor && !med.IsStatic)
            {
                return body.Instructions.Skip(2).First();
            }

            return body.Instructions.First();
        }

        private static Instruction FixReturns(MethodDefinition med, ModuleDefinition mod)
        {
            MethodBody body = med.Body;

            Instruction formallyLastInstruction = body.Instructions.Last();
            Instruction lastLeaveInstruction = null;
            if (med.ReturnType == mod.TypeSystem.Void)
            {
                var instructions = body.Instructions;
                var lastRet = Instruction.Create(OpCodes.Ret);
                instructions.Add(lastRet);

                for (var index = 0; index < instructions.Count - 1; index++)
                {
                    var instruction = instructions[index];
                    if (instruction.OpCode == OpCodes.Ret)
                    {
                        Instruction leaveInstruction = Instruction.Create(OpCodes.Leave, lastRet);
                        if (instruction == formallyLastInstruction)
                        {
                            lastLeaveInstruction = leaveInstruction;
                        }

                        instructions[index] = leaveInstruction;
                    }
                }

                FixBranchTargets(lastLeaveInstruction, formallyLastInstruction, body);
                return lastRet;
            }
            else
            {
                var instructions = body.Instructions;
                var returnVariable = new VariableDefinition("methodTimerReturn", med.ReturnType);
                body.Variables.Add(returnVariable);
                var lastLd = Instruction.Create(OpCodes.Ldloc, returnVariable);
                instructions.Add(lastLd);
                instructions.Add(Instruction.Create(OpCodes.Ret));

                for (var index = 0; index < instructions.Count - 2; index++)
                {
                    var instruction = instructions[index];
                    if (instruction.OpCode == OpCodes.Ret)
                    {
                        Instruction leaveInstruction = Instruction.Create(OpCodes.Leave, lastLd);
                        if (instruction == formallyLastInstruction)
                        {
                            lastLeaveInstruction = leaveInstruction;
                        }

                        instructions[index] = leaveInstruction;
                        instructions.Insert(index, Instruction.Create(OpCodes.Stloc, returnVariable));
                        index++;
                    }
                }

                FixBranchTargets(lastLeaveInstruction, formallyLastInstruction, body);
                return lastLd;
            }
        }

        private static void FixBranchTargets(
          Instruction lastLeaveInstruction,
          Instruction formallyLastRetInstruction,
          MethodBody body)
        {
            for (var index = 0; index < body.Instructions.Count - 2; index++)
            {
                var instruction = body.Instructions[index];
                if (instruction.Operand != null && instruction.Operand == formallyLastRetInstruction)
                {
                    instruction.Operand = lastLeaveInstruction;
                }
            }
        }

        private static Tuple<bool, TestId> IsSequencePointAtStartOfAUnitTest(RunStartParams rsp, Mono.Cecil.Cil.SequencePoint sp, FilePath assemblyPath, Func<DocumentLocation, IEnumerable<DTestCase>> findTest)
        {
            if (sp == null)
            {
                return new Tuple<bool, TestId>(false, null);
            }

            var dl = new DocumentLocation { document = PathBuilder.rebaseCodeFilePath(rsp.Solution.Path, rsp.Solution.SnapshotPath, FilePath.NewFilePath(sp.Document.Url)), line = DocumentCoordinate.NewDocumentCoordinate(sp.StartLine) };
            var test = findTest(dl).FirstOrDefault(t => t.Source.Equals(assemblyPath));
            if (test == null)
            {
                return new Tuple<bool, TestId>(false, null);
            }
            else
            {
                return new Tuple<bool, TestId>(
                    true,
                    new TestId(assemblyPath, dl));
            }
        }

        public static CustomAttribute GetAsyncStateMachineAttribute(this MethodDefinition method)
        {
            var asyncAttribute = method.CustomAttributes.FirstOrDefault(_ => _.AttributeType.Name == "AsyncStateMachineAttribute");
            return asyncAttribute;
        }

        public static bool IsAsync(this MethodDefinition method)
        {
            return GetAsyncStateMachineAttribute(method) != null;
        }
    }
}
