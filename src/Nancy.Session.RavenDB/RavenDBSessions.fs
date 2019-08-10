namespace Nancy.Session.RavenDB

open Nancy.Cryptography
open Nancy.Session.Persistable
open Raven.Client.Documents
open Raven.Client.Documents.Indexes
open Raven.Client.Documents.Operations.Indexes
open Raven.Client.Documents.Session
open System
open System.Collections.Generic

// ---- RavenDB document types ----

/// Record used to persist sessions in the RavenDB session store
type RavenDBSessionDocument =
  { /// The Id of the session
    Id           : string
    /// The date/time this session was last accessed
    LastAccessed : DateTime
    /// The data for the session
    Data         : IDictionary<string, obj>
    }
with
  static member Empty =
    { Id           = null 
      LastAccessed = DateTime.MinValue
      Data         = Dictionary<string, obj> ()
      }
  member this.ToSession () : IPersistableSession =
    upcast BasePersistableSession (Guid.Parse this.Id, this.LastAccessed, this.Data)

// ---- Configuration and Store implementation ----

/// Configuration for RethinkDB session storage
[<AllowNullLiteral>]
type RavenDBSessionConfiguration (store : IDocumentStore, cryptoConfig) =
  inherit BasePersistableSessionConfiguration (cryptoConfig)

  /// Initializes a new instance of the <see cref="RavenDbSessionConfiguration"/> class
  new () = RavenDBSessionConfiguration (null, CryptographyConfiguration.Default)

  /// <summary>
  /// Initializes a new instance of the <see cref="RavenDbSessionConfiguration"/> class
  /// </summary>
  /// <param name="store">The RavenDB document store to use for session storage</param>
  new (store) = RavenDBSessionConfiguration (store, CryptographyConfiguration.Default)

  /// Gets or sets the name of the RavenDB database to use for session storage (blank uses store default)
  member val Database = "" with get, set

  /// Gets or sets the name of the RavenDB collection to use for session storage
  member val Collection = "Sessions" with get, set

  /// Gets or sets the RethinkDB connection to use for session storage
  member val DocumentStore = store with get, set

  /// The session store implementation
  override this.Store with get () = upcast RavenDBSessionStore this


/// The RethinkDB session store
and RavenDBSessionStore (cfg : RavenDBSessionConfiguration) =
  
  /// Create a new RavenDB session
  let newSession () =
    match isNull cfg.Database || cfg.Database = "" with
    | true -> cfg.DocumentStore.OpenAsyncSession ()
    | false -> cfg.DocumentStore.OpenAsyncSession cfg.Database

  /// Await a Task<T>
  let await  task = task |> (Async.AwaitTask >> Async.RunSynchronously)
  let await' task = task |> (Async.AwaitIAsyncResult >> Async.Ignore >> Async.RunSynchronously)

  /// Turn a string into a collection Id (ex. [guid-string] -> "Sessions/[guid-string]")
  let collId = sprintf "%s/%s" cfg.Collection

  /// Create a patch operation to set the last accessed date/time to now
  let setLastAccessedNow (sessId : string) (sess : IAsyncDocumentSession) =
    sess.Advanced.Patch (sessId, (fun s -> s.LastAccessed), DateTime.Now)

  /// Debug text - may be removed before 1.0
  let dbg (text : unit -> string) =
#if DEBUG
    System.Console.WriteLine (sprintf "[RavenSession] %s" (text ()))
#endif
    ()

  /// Create the index on the last accessed date/time if it does not exist
  let ensureIndex () =
    PutIndexesOperation (
      IndexDefinition
        (Name = sprintf "%s/ByLastAccessed" cfg.Collection,
         Maps = HashSet<string> [ sprintf "docs.%s.Select(sess => new { sess.LastAccessed })" cfg.Collection ]))
    |> (cfg.DocumentStore.Maintenance.Send >> ignore)
            
  interface IPersistableSessionStore with
    
    member __.SetUp () =
      ensureIndex ()

    member __.RetrieveSession sessId =
      dbg (fun () -> sprintf "Retrieving session Id %s" sessId)
      use sess = newSession ()
      match (collId >> sess.LoadAsync<RavenDBSessionDocument> >> await >> box) sessId with
      | null ->
          dbg (fun () -> sprintf "Session Id %s not found" sessId)
          null
      | d ->
          dbg (fun () -> sprintf "Found session Id %s" sessId)
          (unbox<RavenDBSessionDocument> d).ToSession ()

    member this.CreateNewSession () =
      let sessId = (Guid.NewGuid >> string) ()
      dbg (fun () -> sprintf "Creating new session with Id %s" sessId)
      use sess = newSession ()
      sess.StoreAsync (
          { RavenDBSessionDocument.Empty with
              Id           = collId sessId
              LastAccessed = DateTime.Now
            }, collId sessId)
      |> await'
      (sess.SaveChangesAsync >> await') ()
      (this :> IPersistableSessionStore).RetrieveSession sessId
  
    member __.UpdateLastAccessed sessId =
      dbg (fun () -> sprintf "Updating last accessed for session Id %s" (string sessId))
      use sess = newSession ()
      setLastAccessedNow ((string >> collId) sessId) sess
      (sess.SaveChangesAsync >> await') ()

    member __.UpdateSession session =
      dbg (fun () -> sprintf "Updating session data for session Id %s" (string session.Id))
      use sess = newSession ()
      let sessId = (string >> collId) session.Id
      match cfg.UseRollingSessions with true -> setLastAccessedNow sessId sess | false -> ()
      sess.Advanced.Patch (sessId, (fun s -> s.Data), session.Items)
      (sess.SaveChangesAsync >> await') ()

    member __.ExpireSessions () =
      dbg (fun () -> "Expiring sessions")
      use sess = newSession ()
      let maxAge = DateTime.Now - cfg.Expiry
      sess.Query<RavenDBSessionDocument>(sprintf "%s/ByLastAccessed" cfg.Collection)
        .Where((fun x -> x.LastAccessed < maxAge), true)
        .ToListAsync ()
      |> await
      |> Seq.iter (fun x -> sess.Delete x.Id)
      (sess.SaveChangesAsync >> await') ()
