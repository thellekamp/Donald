﻿namespace Donald

open System
open System.Data
open System.Data.Common
open System.Threading.Tasks
open System.Threading

[<RequireQualifiedAccess>]
module Db =
    /// Create a new DbUnit instance using the provided IDbConnection.
    let newCommand (commandText : string) (conn : IDbConnection) : DbUnit =
        let cmd = conn.CreateCommand()
        cmd.CommandText <- commandText
        new DbUnit(cmd)

    /// Configure the CancellationToken for the provided DbUnit
    let setCancellationToken (cancellationToken : CancellationToken) (dbunit : DbUnit) : DbUnit =
        dbunit.CancellationToken <- cancellationToken
        dbunit

    /// Configure the CommandBehavior for the provided DbUnit
    let setCommandBehavior (commandBehavior : CommandBehavior) (dbUnit : DbUnit) : DbUnit =
        dbUnit.CommandBehavior <- commandBehavior
        dbUnit

    /// Configure the CommandType for the provided DbUnit
    let setCommandType (commandType : CommandType) (dbUnit : DbUnit) : DbUnit =
        dbUnit.Command.CommandType <- commandType
        dbUnit

    /// Configure the strongly-typed command parameters for the provided DbUnit
    let setParams (param : RawDbParams) (dbUnit : DbUnit) : DbUnit =
        dbUnit.Command.SetParams(DbParams.create param) |> ignore
        dbUnit

    /// Configure the command parameters for the provided DbUnit
    let setParamsRaw (param : (string * obj) list) (dbUnit : DbUnit) : DbUnit =
        dbUnit.Command.SetParamsRaw(param) |> ignore
        dbUnit

    /// Configure the timeout for the provided DbUnit
    let setTimeout (commandTimeout : int) (dbUnit : DbUnit) : DbUnit =
        dbUnit.Command.CommandTimeout <- commandTimeout
        dbUnit

    /// Configure the transaction for the provided DbUnit
    let setTransaction (tran : IDbTransaction) (dbUnit : DbUnit) : DbUnit =
        dbUnit.Command.Transaction <- tran
        dbUnit

    /// Create a new DbUnit instance using the provided IDbTransaction.
    let newCommandForTransaction (commandText : string) (tran : IDbTransaction) : DbUnit =
        tran.Connection |> newCommand commandText |> setTransaction tran

    //
    // Execution model

    let private tryDo (dbUnit : DbUnit) (fn : IDbCommand -> 'a) : 'a =
        dbUnit.Command.Connection.TryOpenConnection()
        let result = fn dbUnit.Command
        (dbUnit :> IDisposable).Dispose()
        result

    /// Execute parameterized query with no results.
    let exec (dbUnit : DbUnit) : unit =
        tryDo dbUnit (fun cmd -> cmd.Exec())

    /// Execute a strongly-type parameterized query many times with no results.
    let execMany (param : RawDbParams list) (dbUnit : DbUnit) : unit =
        tryDo dbUnit (fun cmd ->
            for p in param do cmd.SetParams(DbParams.create p).Exec())

    /// Execute a parameterized query many times with no results.
    let execManyRaw (param : ((string * obj) list) list) (dbUnit : DbUnit) : unit =
        tryDo dbUnit (fun cmd ->
            for p in param do cmd.SetParamsRaw(p).Exec())

    /// Execute scalar query and box the result.
    let scalar (convert : obj -> 'a) (dbUnit : DbUnit) : 'a =
        tryDo dbUnit (fun cmd ->
            let value = cmd.ExecScalar()
            convert value)

    /// Execute paramterized query and return IDataReader
    let read (fn : 'reader -> 'a when 'reader :> IDataReader) (dbUnit : DbUnit) : 'a =
        tryDo dbUnit (fun cmd ->
            use rd = cmd.ExecReader(dbUnit.CommandBehavior) :?> 'reader
            fn rd)

    /// Execute parameterized query, enumerate all records and apply mapping.
    let query (map : 'reader -> 'a when 'reader :> IDataReader) (dbUnit : DbUnit) : 'a list =
        read (fun rd -> [ while rd.Read() do yield map rd ]) dbUnit

    /// Execute paramterized query, read only first record and apply mapping.
    let querySingle (map : 'reader -> 'a when 'reader :> IDataReader) (dbUnit : DbUnit) : 'a option =
        read (fun rd -> if rd.Read() then Some(map rd) else None) dbUnit

    /// Execute an all or none batch of commands.
    let batch (fn : IDbTransaction -> 'a) (conn : IDbConnection) =
        conn.TryOpenConnection()
        use tran = conn.TryBeginTransaction()
        try
            let result = fn tran
            tran.TryCommit()
            result
        with _ ->
            tran.TryRollback()
            reraise ()

    module Async =
        let private tryDoAsync (dbUnit : DbUnit) (fn : DbCommand -> Task<'a>) : Task<'a> =
            task {
                do! dbUnit.Command.Connection.TryOpenConnectionAsync(dbUnit.CancellationToken)
                let! result = fn (dbUnit.Command :?> DbCommand)
                (dbUnit :> IDisposable).Dispose()
                return result }

        /// Asynchronously execute parameterized query with no results.
        let exec (dbUnit : DbUnit) : Task<unit> =
            tryDoAsync dbUnit (fun (cmd : DbCommand) -> task {
                let! _ = cmd.ExecAsync(dbUnit.CancellationToken)
                return () })

        /// Asynchronously execute a strongly-type parameterized query many times with no results.
        let execMany (param : RawDbParams list) (dbUnit : DbUnit) : Task<unit> =
            tryDoAsync dbUnit (fun (cmd : DbCommand) -> task {
                for p in param do
                    do! cmd.SetParams(DbParams.create p).ExecAsync(dbUnit.CancellationToken) })

        /// Asynchronously execute a parameterized query many times with no results.
        let execManyRaw (param : ((string * obj) list) list) (dbUnit : DbUnit) : Task<unit> =
            tryDoAsync dbUnit (fun cmd -> task {
                for p in param do
                    do! cmd.SetParamsRaw(p).ExecAsync(dbUnit.CancellationToken) })

        /// Execute scalar query and box the result.
        let scalar (convert : obj -> 'a) (dbUnit : DbUnit) : Task<'a> =
            tryDoAsync dbUnit (fun (cmd : DbCommand) -> task {
                let! value = cmd.ExecScalarAsync(dbUnit.CancellationToken)
                return convert value })

        /// Asynchronously execute paramterized query and return IDataReader
        let read (fn : 'reader -> 'a when 'reader :> IDataReader) (dbUnit : DbUnit) : Task<'a> =
            tryDoAsync dbUnit (fun (cmd : DbCommand) -> task {
                use! rd = cmd.ExecReaderAsync(dbUnit.CommandBehavior, dbUnit.CancellationToken)
                return fn (rd :?> 'reader) })

        /// Asynchronously execute parameterized query, enumerate all records and apply mapping.
        let query (map : 'reader -> 'a when 'reader :> IDataReader) (dbUnit : DbUnit) : Task<'a list> =
            read (fun rd -> [ while rd.Read() do map rd ]) dbUnit

        /// Asynchronously execute paramterized query, read only first record and apply mapping.
        let querySingle (map : 'reader -> 'a when 'reader :> IDataReader) (dbUnit : DbUnit) : Task<'a option> =
            read (fun rd -> if rd.Read() then Some(map rd) else None) dbUnit

        /// Execute an all or none batch of commands asynchronously.
        let batch (fn : IDbTransaction -> Task<unit>) (conn : IDbConnection) =
            conn.TryOpenConnection()
            use tran = conn.TryBeginTransaction()
            try
                task {
                    let! result = fn tran
                    tran.TryCommit()
                    return result }
            with _ ->
                tran.TryRollback()
                reraise()