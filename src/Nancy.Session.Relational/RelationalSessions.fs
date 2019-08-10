namespace Nancy.Session.Relational

open Nancy.Cryptography
open Nancy.Session.Persistable
open Newtonsoft.Json
open System
open System.Collections.Generic
open System.Configuration
open System.Data.Common

/// Interface for relational configuration options
type IRelationalSessionConfiguration =
  inherit IPersistableSessionConfiguration

  /// The factory to use when creating connections to the database
  abstract Factory : DbProviderFactory

  /// The name of the connection (from connectionStrings in [Web|App].config) or a connection string
  abstract ConnectionString : string

  /// The name of the table where sessions will be stored
  abstract Table : string

  /// The schema within which the table resides
  abstract Schema : string

  /// The SQL dialect to use when establishing tables and indexes
  abstract Dialect : Dialect


/// Configuration for relational sessions
type RelationalSessionConfiguration (factory, connStr, cryptoConfig) =
  inherit BasePersistableSessionConfiguration (cryptoConfig)
  
  /// Derive the dialect from the type of the DbProviderFactory
  let dialect =
    match deriveDialect factory with
    | Some x -> x
    | None ->
        factory.GetType().Name
        |> (sprintf """Unrecognized DbProviderFactory type "%s" received""" >> invalidArg "provider")

  /// Translate a possible connection string name into a connection string
  let toConnStr x =
    match isNullOrEmpty x with
    | true -> ""
    | false ->
        match x.Contains "=" with
        | true -> x
        | false -> 
            match ConfigurationManager.ConnectionStrings.[x] with
            | null -> invalidArg "ConnectionString" (sprintf """Connection string "%s" not found""" x)
            | y -> y.ConnectionString

  /// Backing field for ConnectionString property
  let mutable _connStr = toConnStr connStr

  /// <summary>
  /// Construct a new instance of the <see cref="RelationalSessionConfiguration" /> class
  /// </summary>
  /// <param name="factory">The DbProviderFactory instance to use for this session store</param>
  /// <param name="cryptoConfig">The Nancy cryptography settings to use for the session cookie</param>
  new (factory, cryptoConfig) = RelationalSessionConfiguration (factory, "", cryptoConfig)

  /// <summary>
  /// Construct a new instance of the <see cref="RelationalSessionConfiguration" /> class
  /// </summary>
  /// <param name="factory">The DbProviderFactory instance to use for this session store</param>
  /// <param name="connStr">The connection string (or connection string name) to use for the session store</param>
  new (factory, connStr) = RelationalSessionConfiguration (factory, connStr, CryptographyConfiguration.Default)

  /// <summary>
  /// Construct a new instance of the <see cref="RelationalSessionConfiguration" /> class
  /// </summary>
  /// <param name="factory">The DbProviderFactory instance to use for this session store</param>
  new (factory) = RelationalSessionConfiguration (factory, CryptographyConfiguration.Default)

  /// The DbProviderFactory used by this session store
  member val Factory = factory with get, set
  
  /// The table in which session data will be stored
  member val Table = "NancySession" with get, set
  
  /// The schema in which the session table resides
  member val Schema = "" with get, set
    
  /// The connection string to use (may be set by connection string name from config)
  member __.ConnectionString 
    with get () = _connStr
      and set v = _connStr <- toConnStr v
  
  /// The SQL dialect used by this session store (derived from DbProviderFactory implementation)
  member __.Dialect
    with get () = dialect
      and set (_ : Dialect) = invalidOp "Dialect is derived from the DbProviderFactory type"

  override this.IsValid =
       base.IsValid
    && seq {
         yield (not << isNull)        this.Factory
         yield (not << isNullOrEmpty) this.ConnectionString
         yield (not << isNullOrEmpty) this.Table
         }
       |> Seq.reduce (&&)

  override this.Store = upcast RelationalSessionStore this

  interface IRelationalSessionConfiguration with
    member this.Factory          = this.Factory
    member this.ConnectionString = this.ConnectionString
    member this.Table            = this.Table
    member this.Schema           = this.Schema
    member this.Dialect          = this.Dialect


/// Session store implementation for Entity Framework
and RelationalSessionStore (cfg : IRelationalSessionConfiguration) =

  /// If the schema was specified, qualify access to the table
  let table = qualifiedTable cfg.Schema cfg.Table
  
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
  let conn () = createConn cfg.Factory cfg.ConnectionString

  /// Add "now" to the given SQL command, translating date/time to ticks for SQLite
  let addNow cmd =
    match cfg.Dialect with
    | Dialect.SQLite -> addParam cmd DateTime.Now.Ticks
    | _ -> addParam cmd DateTime.Now

  /// Log access point
  let log = LogUtils<RelationalSessionStore> cfg.LogLevel

  interface IPersistableSessionStore with
    
    member __.SetUp () =
      establishDataStore cfg.Factory cfg.ConnectionString cfg.Schema cfg.Table
      |> withDialect cfg.Dialect

    member __.RetrieveSession sessId =
      let parseLastAccessed (rdr : DbDataReader) =
        match cfg.Dialect with Dialect.SQLite -> DateTime (rdr.GetInt64 1) | _ -> rdr.GetDateTime 1
      log.dbug (fun () -> sprintf "Retrieving session Id %s" sessId)
      use conn = conn ()
      use cmd  = createCmd conn selectSql
      addParam cmd sessId
      use rdr = cmd.ExecuteReaderAsync () |> await
      match rdr.ReadAsync () |> await with
      | true ->
          log.dbug (fun () -> sprintf "Found session Id %s" sessId)
          upcast BasePersistableSession
            ((rdr.GetString >> Guid.Parse) 0,
             parseLastAccessed rdr,
             (rdr.GetString >> JsonConvert.DeserializeObject<Dictionary<string, obj>>) 2)
      | _ ->
          log.dbug (fun () -> sprintf "Session Id %s not found" sessId)
          null

    member __.CreateNewSession () =
      let sessId = Guid.NewGuid ()
      log.info (fun () -> sprintf "Creating new session with Id %s" (string sessId))
      let sess = BasePersistableSession (sessId, DateTime.Now, Dictionary<string, obj> ())
      use conn = conn ()
      use cmd  = createCmd conn createSql
      addParam cmd (string sess.Id)
      addNow   cmd
      addParam cmd (dataToJson sess.Items)
      runCmd cmd
      upcast sess

    member __.UpdateLastAccessed sessId =
      log.dbug (fun () -> sprintf "Updating last accessed for session Id %s" (string sessId))
      use conn = conn ()
      use cmd  = createCmd conn lastAccessUpdateSql
      addNow   cmd
      addParam cmd (string id)
      runCmd cmd

    member __.UpdateSession session =
      log.dbug (fun () -> sprintf "Updating session data for session Id %s" (string session.Id))
      use conn = conn ()
      use cmd = createCmd conn (match cfg.UseRollingSessions with true -> dataAndAccessUpdateSql | _ -> dataUpdateSql)
      match cfg.UseRollingSessions with true -> addNow cmd | _ -> ()
      addParam cmd (dataToJson session.Items)
      addParam cmd (string session.Id)
      runCmd cmd

    member __.ExpireSessions () =
      log.dbug (fun () -> "Expiring sessions")
      use conn = conn ()
      use cmd  = createCmd conn expireSql
      addParam cmd (DateTime.Now - cfg.Expiry)
      runCmd cmd
