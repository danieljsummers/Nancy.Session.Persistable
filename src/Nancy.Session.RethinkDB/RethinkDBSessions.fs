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
      Data = Dictionary<string, obj>() }
  member this.ToSession () : IPersistableSession =
    upcast BasePersistableSession(Guid.Parse this.Id, this.LastAccessed, this.Data)

/// Used to update the last accessed date/time for a session
type LastAccessUpdateDocument = { LastAccessed : DateTime }
with static member Now = { LastAccessed = DateTime.Now }

// ---- Configuration and Store implementation ----

/// Configuration for RethinkDB session storage
[<AllowNullLiteral>]
type RethinkDBSessionConfiguration (conn : IConnection, cryptoConfig : CryptographyConfiguration) =
  inherit BasePersistableSessionConfiguration(cryptoConfig)

  /// Initializes a new instance of the <see cref="RethinkDbSessionConfiguration"/> class.
  new() = RethinkDBSessionConfiguration(null, CryptographyConfiguration.Default)

  /// <summary>
  /// Initializes a new instance of the <see cref="RethinkDbSessionConfiguration"/> class.
  /// </summary>
  /// <param name="connection">The RethinkDB connection to use for session storage</param>
  new(conn) = RethinkDBSessionConfiguration(conn, CryptographyConfiguration.Default)

  /// Gets or sets the name of the RethinkDB database to use for session storage
  member val Database = "NancySession" with get, set

  /// Gets or sets the name of the RethinkDB table to use for session storage
  member val Table = "Session" with get, set

  /// Gets or sets the RethinkDB connection to use for session storage
  member val Connection = conn with get, set

  /// The session store implementation
  override this.Store with get() = upcast RethinkDBSessionStore(this)


/// The RethinkDB session store
and RethinkDBSessionStore(cfg : RethinkDBSessionConfiguration) =
  
  /// Await a Task<T>
  let await task = task |> Async.AwaitTask |> Async.RunSynchronously

  /// Debug text - may be removed before 1.0
  let dbg (text : unit -> string) =
#if DEBUG
    System.Console.WriteLine (sprintf "[RethinkSession] %s" (text ()))
#endif
    ()

  /// Check the result of a RethinkDB operation
  let checkResult format (msg : unit -> string) (result : Model.Result) =
    match uint64 0 = result.Errors with true -> () | _ -> invalidOp (sprintf format (msg()) result.FirstError)

  let r = RethinkDB.R

  /// Shorthand to get the session table
  let table () = r.Db(cfg.Database).Table(cfg.Table)

  /// Create the database if it does not exist
  let databaseCheck () =
    dbg (fun () -> sprintf "Checking for the existence of database %s" cfg.Database)
    match r.DbList().RunAtomAsync<List<string>>(cfg.Connection)
          |> await
          |> Seq.exists (fun db -> db = cfg.Database) with
    | true -> ()
    | _ -> dbg (fun () -> sprintf "Creating database %s" cfg.Database)
           r.DbCreate(cfg.Database).RunResultAsync(cfg.Connection)
           |> await
           |> checkResult "Could not create RethinkDB session store database %s: %s" (fun () -> cfg.Database)

  /// Create the table if it does not exist
  let tableCheck () =
    dbg (fun () -> sprintf "Checking for the existence of table %s.%s" cfg.Database cfg.Table)
    match r.Db(cfg.Database).TableList().RunAtomAsync<List<string>>(cfg.Connection)
          |> await
          |> Seq.exists (fun tbl -> tbl = cfg.Table) with
    | true -> ()
    | _ -> dbg (fun () -> sprintf "Creating table %s.%s" cfg.Database cfg.Table)
           r.Db(cfg.Database).TableCreate(cfg.Table).RunResultAsync(cfg.Connection)
           |> await
           |> checkResult "Could not create RethinkDB session store table %s: %s"
                          (fun () -> sprintf "%s.%s" cfg.Database cfg.Table)

  /// Create the index on the last accessed date/time if it does not exist
  let indexCheck () =
    let idxName = "LastAccessed"
    dbg (fun () -> sprintf "Checking for the existence of index %s" idxName)
    match table().IndexList().RunAtomAsync<List<string>>(cfg.Connection)
          |> await
          |> Seq.exists (fun idx -> idx = idxName) with
    | true -> ()
    | _ -> dbg (fun () -> sprintf "Creating index %s" idxName)
           table().IndexCreate(idxName).RunResultAsync(cfg.Connection)
           |> await
           |> checkResult "Could not create last accessed index on RethinkDB session store table %s: %s"
                          (fun () -> sprintf "%s.%s" cfg.Database cfg.Table)

  interface IPersistableSessionStore with
    
    member this.SetUp () =
      databaseCheck ()
      tableCheck    ()
      indexCheck    ()

    member this.RetrieveSession id =
      dbg (fun () -> sprintf "Retrieving session Id %s" id)
      match table().Get(id).RunAtomAsync<RethinkDBSessionDocument>(cfg.Connection)
            |> await
            |> box with
      | null -> dbg (fun () -> sprintf "Session Id %s not found" id)
                null
      | d -> dbg (fun () -> sprintf "Found session Id %s" id)
             let doc : RethinkDBSessionDocument = unbox d
             doc.ToSession ()

    member this.CreateNewSession () =
      let id = string (Guid.NewGuid())
      dbg (fun () -> sprintf "Creating new session with Id %s" id)
      table()
        .Insert( { RethinkDBSessionDocument.Empty with
                     Id           = id
                     LastAccessed = DateTime.Now } )
        .RunResultAsync(cfg.Connection)
      |> await
      |> checkResult "Could not create new session Id %s: %s" (fun () -> id)
      (this :> IPersistableSessionStore).RetrieveSession id
  
    member this.UpdateLastAccessed id =
      dbg (fun () -> sprintf "Updating last accessed for session Id %s" (string id))
      table().Get(string id).Update(LastAccessUpdateDocument.Now).RunResultAsync(cfg.Connection)
      |> await
      |> checkResult "Could not update last access for session Id %s: %s" (fun () -> string id)

    member this.UpdateSession session =
      dbg (fun () -> sprintf "Updating session data for session Id %s" (string session.Id))
      table().Get(string session.Id)
        .Replace( { Id           = string session.Id
                    LastAccessed = match cfg.UseRollingSessions with true -> DateTime.Now | _ -> session.LastAccessed
                    Data         = session.Items } )
        .RunResultAsync(cfg.Connection)
      |> await
      |> checkResult "Unable to save data for session Id %s: %s" (fun () -> string session.Id)

    member this.ExpireSessions () =
      dbg (fun () -> "Expiring sessions")
      table().Between(r.Minval (), DateTime.Now - cfg.Expiry)
        .OptArg("index", "LastAccessed")
        .Delete()
        .RunResultAsync(cfg.Connection)
      |> await
      |> checkResult "Error expiring sessions: %s%s" (fun () -> "")
