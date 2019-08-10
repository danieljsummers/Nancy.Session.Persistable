namespace Nancy.Session.RethinkDB

open Nancy.Cryptography
open Nancy.Session.Persistable
open Newtonsoft.Json
open RethinkDb.Driver
open RethinkDb.Driver.Net
open System
open System.Collections.Generic

// ---- RethinkDB document types ----

/// Record used to persist sessions in the RethinkDB session store
type RethinkDBSessionDocument =
  { /// The Id of the session
    [<JsonProperty("id")>]
    Id : string
    /// The date/time this session was last accessed
    LastAccessed : DateTime
    /// The data for the session
    Data : IDictionary<string, obj> }
with
  static member Empty =
    { Id = null 
      LastAccessed = DateTime.MinValue
      Data = Dictionary<string, obj> () }
  member this.ToSession () : IPersistableSession =
    upcast BasePersistableSession (Guid.Parse this.Id, this.LastAccessed, this.Data)

/// Used to update the last accessed date/time for a session
type LastAccessUpdateDocument = { LastAccessed : DateTime }
with static member Now = { LastAccessed = DateTime.Now }

// ---- Configuration and Store implementation ----

/// Configuration for RethinkDB session storage
[<AllowNullLiteral>]
type RethinkDBSessionConfiguration (conn : IConnection, cryptoConfig : CryptographyConfiguration) =
  inherit BasePersistableSessionConfiguration (cryptoConfig)

  /// Initializes a new instance of the <see cref="RethinkDbSessionConfiguration"/> class.
  new () = RethinkDBSessionConfiguration (null, CryptographyConfiguration.Default)

  /// <summary>
  /// Initializes a new instance of the <see cref="RethinkDbSessionConfiguration"/> class.
  /// </summary>
  /// <param name="connection">The RethinkDB connection to use for session storage</param>
  new (conn) = RethinkDBSessionConfiguration (conn, CryptographyConfiguration.Default)

  /// Gets or sets the name of the RethinkDB database to use for session storage
  member val Database = "NancySession" with get, set

  /// Gets or sets the name of the RethinkDB table to use for session storage
  member val Table = "Session" with get, set

  /// Gets or sets the RethinkDB connection to use for session storage
  member val Connection = conn with get, set

  /// The session store implementation
  override this.Store with get () = upcast RethinkDBSessionStore this


/// The RethinkDB session store
and RethinkDBSessionStore (cfg : RethinkDBSessionConfiguration) =
  
  /// Await a Task<T>
  let await task = task |> (Async.AwaitTask >> Async.RunSynchronously)

  /// Log access point
  let log = LogUtils<RethinkDBSessionStore> cfg.LogLevel

  /// Check the result of a RethinkDB operation
  let checkResult format (msg : unit -> string) (result : Model.Result) =
    match result.Errors with 0UL -> () | _ -> (sprintf format (msg ()) >> invalidOp) result.FirstError

  let r = RethinkDB.R

  /// Shorthand to get the session table
  let table () = r.Db(cfg.Database).Table cfg.Table

  /// Create the database if it does not exist
  let databaseCheck () =
    log.dbug (fun () -> sprintf "Checking for the existence of database %s" cfg.Database)
    match (r.DbList().RunAtomAsync<List<string>> >> await >> Seq.exists (fun db -> db = cfg.Database))
            cfg.Connection with
    | true -> ()
    | _ ->
        log.dbug (fun () -> sprintf "Creating database %s" cfg.Database)
        (r.DbCreate(cfg.Database).RunWriteAsync >> await
          >> checkResult "Could not create RethinkDB session store database %s: %s" (fun () -> cfg.Database))
            cfg.Connection

  /// Create the table if it does not exist
  let tableCheck () =
    let tblName = sprintf "%s.%s" cfg.Database cfg.Table
    log.dbug (fun () -> sprintf "Checking for the existence of table %s" tblName)
    match (r.Db(cfg.Database).TableList().RunAtomAsync<List<string>> >> await >> Seq.exists (fun t -> t = cfg.Table))
            cfg.Connection with
    | true -> ()
    | _ ->
        log.dbug (fun () -> sprintf "Creating table %s" tblName)
        (r.Db(cfg.Database).TableCreate(cfg.Table).RunWriteAsync >> await
          >> checkResult "Could not create RethinkDB session store table %s: %s" (fun () -> tblName))
            cfg.Connection

  /// Create the index on the last accessed date/time if it does not exist
  let indexCheck () =
    let idxName = "LastAccessed"
    log.dbug (fun () -> sprintf "Checking for the existence of index %s" idxName)
    match (table().IndexList().RunAtomAsync<List<string>> >> await >> Seq.exists (fun idx -> idx = idxName))
            cfg.Connection with
    | true -> ()
    | _ ->
        log.dbug (fun () -> sprintf "Creating index %s" idxName)
        (table().IndexCreate(idxName).RunWriteAsync >> await
          >> checkResult "Could not create last accessed index on RethinkDB session store table %s: %s"
                          (fun () -> sprintf "%s.%s" cfg.Database cfg.Table))
            cfg.Connection

  interface IPersistableSessionStore with
    
    member __.SetUp () =
      databaseCheck ()
      tableCheck    ()
      indexCheck    ()

    member __.RetrieveSession id =
      log.dbug (fun () -> sprintf "Retrieving session Id %s" id)
      match (table().Get(id).RunAtomAsync<RethinkDBSessionDocument> >> await >> box) cfg.Connection with
      | null ->
          log.dbug (fun () -> sprintf "Session Id %s not found" id)
          null
      | d ->
          log.dbug (fun () -> sprintf "Found session Id %s" id)
          (unbox<RethinkDBSessionDocument> d).ToSession ()

    member this.CreateNewSession () =
      let sessId = (Guid.NewGuid >> string) ()
      log.info (fun () -> sprintf "Creating new session with Id %s" sessId)
      (table()
        .Insert(
          { RethinkDBSessionDocument.Empty with
              Id           = sessId
              LastAccessed = DateTime.Now })
        .RunWriteAsync >> await >> checkResult "Could not create new session Id %s: %s" (fun () -> sessId))
          cfg.Connection
      (this :> IPersistableSessionStore).RetrieveSession sessId
  
    member __.UpdateLastAccessed sessId =
      log.dbug (fun () -> sprintf "Updating last accessed for session Id %s" (string sessId))
      (table().Get(string sessId).Update(LastAccessUpdateDocument.Now).RunWriteAsync >> await
        >> checkResult "Could not update last access for session Id %s: %s" (fun () -> string id))
          cfg.Connection

    member __.UpdateSession session =
      log.dbug (fun () -> sprintf "Updating session data for session Id %s" (string session.Id))
      (table().Get(string session.Id)
        .Replace(
          { Id           = string session.Id
            LastAccessed = match cfg.UseRollingSessions with true -> DateTime.Now | _ -> session.LastAccessed
            Data         = session.Items })
        .RunWriteAsync >> await
        >> checkResult "Unable to save data for session Id %s: %s" (fun () -> string session.Id))
          cfg.Connection

    member __.ExpireSessions () =
      log.dbug (fun () -> "Expiring sessions")
      (table()
        .Between(r.Minval (), DateTime.Now - cfg.Expiry)
        .OptArg("index", "LastAccessed")
        .Delete()
        .RunWriteAsync >> await >> checkResult "Error expiring sessions: %s%s" (fun () -> ""))
          cfg.Connection
