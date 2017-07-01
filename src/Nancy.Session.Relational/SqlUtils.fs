[<AutoOpen>]
module internal Nancy.Session.Relational.SqlUtils

/// Dialect / SQL command map
type internal SqlCommands = {
  Dialect         : Dialect
  ProviderFactory : string
  DefaultSchema   : string option
  TableExistence  : string
  CreateTable     : string
}

/// Lookup for SQL dialect-specific implementations
let private sqlLookup =
  [ { Dialect         = Dialect.SqlServer
      ProviderFactory = "System.Data.SqlClient"
      DefaultSchema   = Some "SELECT SCHEMA_NAME()"
      TableExistence  = "SELECT COUNT(table_name) FROM information_schema.tables WHERE table_schema = ? AND table_name = ?"
      CreateTable     = """
                        CREATE TABLE %% (
                          id VARCHAR(36) PRIMARY KEY NOT NULL,
                          last_accessed SMALLDATETIME NOT NULL INDEX idx_session_last_accessed,
                          data VARCHAR(MAX) NOT NULL
                        );
                        """ }
    { Dialect         = Dialect.PostgreSql
      ProviderFactory = "Npgsql"
      DefaultSchema   = Some "SELECT CURRENT_SCHEMA()"
      TableExistence  = "SELECT COUNT(tablename) FROM pg_tables WHERE schemaname = ? AND tablename = ?"
      CreateTable     = """
                        CREATE TABLE %% (
                          id VARCHAR(36) PRIMARY KEY NOT NULL,
                          last_accessed TIMESTAMP WITHOUT TIME ZONE NOT NULL,
                          data TEXT NOT NULL
                        );
                        CREATE INDEX idx_session_last_accessed ON %% (last_accessed); 
                        """ }
    { Dialect         = Dialect.MySql
      ProviderFactory = "MySql.Data.MySqlClient"
      DefaultSchema   = Some "SELECT DATABASE()"
      TableExistence  = "SELECT COUNT(table_name) FROM information_schema.tables WHERE table_schema = ? AND table_name = ?"
      CreateTable     = """
                        CREATE TABLE %% (
                          id VARCHAR(36) PRIMARY KEY NOT NULL,
                          last_accessed DATETIME NOT NULL,
                          data TEXT NOT NULL
                        ),
                        INDEX idx_session_last_accessed (last_accessed);
                        """ }
    { Dialect         = Dialect.SQLite
      ProviderFactory = "System.Data.SQLite"
      DefaultSchema   = None
      TableExistence  = "SELECT COUNT(name) FROM sqlite_master WHERE type = 'table' and name = ?"
      CreateTable     = """
                        CREATE TABLE %% (
                          id VARCHAR(36) PRIMARY KEY NOT NULL,
                          last_accessed INTEGER NOT NULL,
                          data TEXT NOT NULL
                        );
                        CREATE INDEX idx_session_last_accessed ON %% (last_accessed);
                        """ }
  ]

/// Get the SQL details for a given dialect
let private forDialect dialect =
  try sqlLookup
      |> List.filter (fun s -> s.Dialect = dialect)
      |> List.exactlyOne
  with _ -> invalidArg "RelationalSessionConfiguration.Dialect" "Invalid SQL dialect configured"
 
let internal withDialect dialect (f : SqlCommands -> 'a) = forDialect dialect |> f

open System
open System.Data.Common

/// Await a task, returning its result
let await task = task |> (Async.AwaitTask >> Async.RunSynchronously)

/// Await a void task
let await' (task : System.Threading.Tasks.Task) =
  task |> (Async.AwaitIAsyncResult >> Async.Ignore >> Async.RunSynchronously)

/// Get an open connection to the data store
let createConn connStr cmds =
  let c =
#if NET452
    DbProviderFactories.GetFactory((forDialect cmds.Dialect).ProviderFactory).CreateConnection ()
#else
#if NETSTANDARD1_6
    match cmds.Dialect with
    | Dialect.SqlServer -> new System.Data.SqlClient.SqlConnection ()
    | d -> failwithf "Dialect %A not supported" d
#else
    failwithf "Framework not supported"
#endif
#endif
  c.ConnectionString <- connStr
  c.OpenAsync () |> await'
  c

/// Create a command on the given connection
let createCmd (conn : DbConnection) sql =
  let c = conn.CreateCommand ()
  c.CommandText <- sql
  c

/// Add a parameter to the given statement (command)
let addParam (stmt : DbCommand) value =
  let p = stmt.CreateParameter ()
  p.Value <- value
  stmt.Parameters.Add p |> ignore

/// Run a command, ignoring the results
let runCmd (cmd : DbCommand) = cmd.ExecuteNonQueryAsync () |> (await >> ignore)

/// Obtain the default schema (used to check for table existence)
let defaultSchema (conn : DbConnection) schema defaultSql =
  match String.IsNullOrEmpty schema with
  | true ->
      use cmd = conn.CreateCommand ()
      cmd.CommandText <- defaultSql
      cmd.ExecuteScalarAsync ()
      |> (await >> string)
  | _ -> schema

/// Determine if the configured session table exists
let tableExists (conn : DbConnection) schema table cmds =
  use cmd = conn.CreateCommand ()
  cmd.CommandText <-
    match cmds.Dialect with
    | Dialect.SQLite ->
        // Schema = attached database; must change table, not pass parameter
        match String.IsNullOrEmpty schema with
        | true -> cmds.TableExistence
        | _ -> cmds.TableExistence.Replace ("FROM ", sprintf "FROM %s." schema) 
    | _ -> cmds.TableExistence
  match cmds.DefaultSchema with Some s -> addParam cmd (defaultSchema conn schema s) | None -> ()
  addParam cmd table
  1 = (cmd.ExecuteScalarAsync () |> (await >> Convert.ToInt32))

/// Create a qualified table name if the schema is present, and an unqualified one if it is not
let qualifiedTable schema table =
  match System.String.IsNullOrEmpty schema with true -> table | _ -> sprintf "%s.%s" schema table

let establishDataStore connectionString schema table cmds =
  use conn = createConn connectionString cmds
  match tableExists conn schema table cmds with
  | true -> ()
  | _ ->
      use cmd = conn.CreateCommand ()
      cmd.CommandText <- cmds.CreateTable.Replace ("%%", qualifiedTable schema table)
      runCmd cmd
