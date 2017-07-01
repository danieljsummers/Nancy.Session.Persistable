namespace Nancy.Session.Relational

open Nancy.Cryptography
open Nancy.Session.Persistable
open Newtonsoft.Json
open System
open System.Collections.Generic
open System.Data
open System.Data.Common
open System.Linq

/// Interface for relational configuration options
type IRelationalSessionConfiguration =
  inherit IPersistableSessionConfiguration

  /// The name of the connection (from connectionStrings in [Web|App].config) or a connection string
  abstract NameOrConnectionString : string

  /// The name of the table where sessions will be stored
  abstract Table : string

  /// The schema within which the table resides
  abstract Schema : string

  /// The SQL dialect to use when establishing tables and indexes
  abstract Dialect : Dialect


/// Configuration for relational sessions
and RelationalSessionConfiguration (nameOrConnectionString, cryptoConfig) =
  inherit BasePersistableSessionConfiguration (cryptoConfig)
  
  new (cryptoConfig) = RelationalSessionConfiguration ("", cryptoConfig)

  new (nameOrConnectionString) =
    RelationalSessionConfiguration (nameOrConnectionString, CryptographyConfiguration.Default)

  new () = RelationalSessionConfiguration CryptographyConfiguration.Default

  member val NameOrConnectionString = nameOrConnectionString with get, set
  member val Table                  = "NancySession"         with get, set
  member val Schema                 = ""                     with get, set
  member val Dialect                = Dialect.SqlServer      with get, set

  override this.IsValid =
       base.IsValid
    && seq {
         yield (not << String.IsNullOrEmpty) this.NameOrConnectionString
         yield (not << String.IsNullOrEmpty) this.Table
         yield Enum.IsDefined (typeof<Dialect>, this.Dialect)
         }
       |> Seq.reduce (&&)

  override this.Store = upcast RelationalSessionStore this

  interface IRelationalSessionConfiguration with
    member this.NameOrConnectionString = this.NameOrConnectionString
    member this.Table                  = this.Table
    member this.Schema                 = this.Schema
    member this.Dialect                = this.Dialect


/// Session store implementation for Entity Framework
and RelationalSessionStore (cfg : IRelationalSessionConfiguration) =

  /// If the schema was specified, qualify access to the table
  let table = match String.IsNullOrEmpty cfg.Schema with true -> cfg.Table | _ -> sprintf "%s.%s" cfg.Schema cfg.Table
  
  /// SQL to select a session
  let selectSql = sprintf "SELECT id, last_accessed, data FROM %s WHERE id = ?" table

  /// SQL to insert a new session
  let createSql = sprintf "INSERT INTO %s (id, last_accessed, data) VALUES (?, ?, ?)" table

  /// SQL to update the last access date/time of a session
  let lastAccessUpdateSql = sprintf "UPDATE %s SET last_accessed = ? WHERE id = ?" table

  /// SQL to update the data and the last access date/time of a session
  let dataAndAccessUpdateSql = sprintf "UPDATE %s SET last_accessed = ?, data = ? WHERE id = ?" table

  /// SQL to update just the data of a session
  let dataUpdateSql = sprintf "UPDATE %s SET data = ? WHERE id = ?" table

  /// SQL to delete expired sessions
  let expireSql = sprintf "DELETE FROM %s WHERE last_accessed < ?" table

  /// Serialize the data to JSON
  let dataToJson data = JsonConvert.SerializeObject data

  /// Shorthand for a new connection
  let conn () =
    createConn cfg.NameOrConnectionString
    |> withDialect cfg.Dialect

  /// Add "now" to the given SQL command, translating date/time to ticks for SQLite
  let addNow cmd =
    match cfg.Dialect with
    | Dialect.SQLite -> addParam cmd DateTime.Now.Ticks
    | _ -> addParam cmd DateTime.Now

  interface IPersistableSessionStore with
    
    member __.SetUp () =
      establishDataStore cfg.NameOrConnectionString cfg.Schema cfg.Table
      |> withDialect cfg.Dialect

    member __.RetrieveSession sessionId =
      let parseLastAccessed (rdr : DbDataReader) =
        match cfg.Dialect with Dialect.SQLite -> DateTime(rdr.GetInt64 1) | _ -> rdr.GetDateTime 1
      use conn = conn ()
      use cmd  = createCmd conn selectSql
      addParam cmd sessionId
      use rdr = cmd.ExecuteReaderAsync () |> await
      match rdr.HasRows with
      | true ->
          rdr.ReadAsync () |> (await >> ignore)
          upcast BasePersistableSession
            ((rdr.GetString >> Guid.Parse) 0,
             parseLastAccessed rdr,
             (rdr.GetString >> JsonConvert.DeserializeObject<Dictionary<string, obj>>) 2)
      | _ -> null

    member __.CreateNewSession () =
      let sess = BasePersistableSession (Guid.NewGuid(), DateTime.Now, Dictionary<string, obj>())
      use conn = conn ()
      use cmd  = createCmd conn createSql
      addParam cmd (string sess.Id)
      addNow   cmd
      addParam cmd (dataToJson sess.Items)
      runCmd cmd
      upcast sess

    member __.UpdateLastAccessed id =
      use conn = conn ()
      use cmd  = createCmd conn lastAccessUpdateSql
      addNow   cmd
      addParam cmd (string id)
      runCmd cmd

    member __.UpdateSession session =
      use conn = conn ()
      use cmd = createCmd conn (match cfg.UseRollingSessions with true -> dataAndAccessUpdateSql | _ -> dataUpdateSql)
      match cfg.UseRollingSessions with true -> addNow cmd | _ -> ()
      addParam cmd (dataToJson session.Items)
      addParam cmd (string session.Id)
      runCmd cmd

    member __.ExpireSessions () =
      use conn = conn ()
      use cmd  = createCmd conn expireSql
      addParam cmd (DateTime.Now - cfg.Expiry)
      runCmd cmd
