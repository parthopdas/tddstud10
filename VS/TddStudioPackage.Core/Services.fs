﻿namespace R4nd0mApps.TddStud10.Hosts.VS.TddStudioPackage.Core

open System
open System.Runtime.CompilerServices
open Microsoft.VisualStudio.Shell

// NOTE: We should ideally return Option<'T> from these functions. Do it when we get a chance next.
[<Extension>]
type public Services = 
    
    [<Extension>]
    static member public GetService<'TS, 'TI when 'TI : null>(sp : IServiceProvider) : 'TI = 
        let logger = R4nd0mApps.TddStud10.Logger.LoggerFactory.logger

        let s = sp.GetService(typeof<'TS>)
        match s with
        | null -> 
            logger.logErrorf "Unable to query for service of type %s." typeof<'TS>.FullName
            null
        | :? 'TI as i -> i
        | _ -> 
            logger.logErrorf "Cannot cast service '%s' to '%s'." typeof<'TS>.FullName typeof<'TI>.FullName
            null

    [<Extension>]
    static member public GetService<'T when 'T : null>(sp : IServiceProvider) : 'T = 
        Services.GetService<'T, 'T>(sp)

    static member public GetService<'T when 'T : null>() : 'T = 
        Services.GetService<'T, 'T>(ServiceProvider.GlobalProvider)

    static member public GetService<'TS, 'TI when 'TI : null>() : 'TI = 
        Services.GetService<'TS, 'TI>(ServiceProvider.GlobalProvider)
