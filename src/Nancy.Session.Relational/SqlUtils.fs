[<AutoOpen>]
module internal Nancy.Session.Relational.SqlUtils

/// Dialect / SQL command map
type internal SqlCommands =
  { Dialect         : Dialect
    ProviderFactory : string
    DefaultSchema   : string option
    TableExistence  : string
    CreateTable     : string
    }

/// Lookup for SQL dialect-specific implementations
let private sqlLookup =
  [ { Dialect         = Dialect.SqlServer
      ProviderFactory = "SqlClientFactory"
      DefaultSchema   = Some "SELECT SCHEMA_NAME()"
      TableExistence  = "SELECT COUNT(table_name) FROM information_schema.tables WHERE table_schema = ? AND table_name = ?"
      CreateTable     = """
                        CREATE TABLE %% (
                          id VARCHAR(36) PRIMARY KEY NOT NULL,
                          last_accessed SMALLDATETIME NOT NULL INDEX idx_session_last_accessed,
                          data VARCHAR(MAX) NOT NULL
                        );
                        """
      }
    { Dialect         = Dialect.PostgreSql
      ProviderFactory = "NpgsqlFactory"
      DefaultSchema   = Some "SELECT CURRENT_SCHEMA()"
      TableExistence  = "SELECT COUNT(tablename) FROM pg_tables WHERE schemaname = ? AND tablename = ?"
      CreateTable     = """
                        CREATE TABLE %% (
                          id VARCHAR(36) PRIMARY KEY NOT NULL,
                          last_accessed TIMESTAMP WITHOUT TIME ZONE NOT NULL,
                          data TEXT NOT NULL
                        );
                        CREATE INDEX idx_session_last_accessed ON %% (last_accessed); 
                        """
      }
    { Dialect         = Dialect.MySql
      ProviderFactory = "MySqlClientFactory"
      DefaultSchema   = Some "SELECT DATABASE()"
      TableExistence  = "SELECT COUNT(table_name) FROM information_schema.tables WHERE table_schema = ? AND table_name = ?"
      CreateTable     = """
                        CREATE TABLE %% (
                          id VARCHAR(36) PRIMARY KEY NOT NULL,
                          last_accessed DATETIME NOT NULL,
                          data TEXT NOT NULL
                        ),
                        INDEX idx_session_last_accessed (last_accessed);
                        """
      }
    { Dialect         = Dialect.SQLite
      ProviderFactory = "SQLiteFactory"
      DefaultSchema   = None
      TableExistence  = "SELECT COUNT(name) FROM sqlite_master WHERE type = 'table' and name = ?"
      CreateTable     = """
                        CREATE TABLE %% (
                          id VARCHAR(36) PRIMARY KEY NOT NULL,
                          last_accessed INTEGER NOT NULL,
                          data TEXT NOT NULL
                        );
                        CREATE INDEX idx_session_last_accessed ON %% (last_accessed);
                        """
      }
    ]

/// Get the SQL details for a given dialect
let private forDialect dialect =
  try sqlLookup
      |> List.filter (fun s -> s.Dialect = dialect)
      |> List.exactlyOne
  with _ -> invalidArg "RelationalSessionConfiguration.Dialect" "Invalid SQL dialect configured"
 
let internal withDialect dialect (f : SqlCommands -> 'a) = (forDialect >> f) dialect

open System
open System.Data.Common

/// Derive the dialect based on the name of the DbProviderFactory implementation
let deriveDialect (factory : DbProviderFactory) =
  sqlLookup
  |> List.tryFind (fun cmds -> cmds.ProviderFactory = factory.GetType().Name)
  |> Option.map (fun cmds -> cmds.Dialect)

/// Await a task, returning its result
let await  task = task |> (Async.AwaitTask >> Async.RunSynchronously)

/// Await a void task
let await' task = task |> (Async.AwaitIAsyncResult >> Async.Ignore >> Async.RunSynchronously)

/// Is a string null or empty?
let isNullOrEmpty x = isNull x || x = ""

/// Get an open connection to the data store
let createConn (factory : DbProviderFactory) connStr =
  let c = factory.CreateConnection ()
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
        match isNullOrEmpty schema with
        | true -> cmds.TableExistence
        | _ -> cmds.TableExistence.Replace ("FROM ", sprintf "FROM %s." schema) 
    | _ -> cmds.TableExistence
  match cmds.DefaultSchema with Some s -> addParam cmd (defaultSchema conn schema s) | None -> ()
  addParam cmd table
  1 = (cmd.ExecuteScalarAsync () |> (await >> Convert.ToInt32))

/// Create a qualified table name if the schema is present, and an unqualified one if it is not
let qualifiedTable schema table =
  match isNullOrEmpty schema with true -> table | _ -> sprintf "%s.%s" schema table

/// Make sure that the session table exists
let establishDataStore provider connectionString schema table cmds =
  use conn = createConn provider connectionString
  match tableExists conn schema table cmds with
  | true -> ()
  | _ ->
      use cmd = conn.CreateCommand ()
      cmd.CommandText <- cmds.CreateTable.Replace ("%%", qualifiedTable schema table)
      runCmd cmd
