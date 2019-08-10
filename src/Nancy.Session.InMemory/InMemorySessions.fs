namespace Nancy.Session.InMemory

open Nancy.Cryptography
open Nancy.Session.Persistable
open System
open System.Collections.Generic

/// The representation of a stored session
type StoredSession =
  { Id           : Guid
    LastAccessed : DateTime
    Data         : IDictionary<string, obj>
    }
with
  static member From (session : IPersistableSession) =
    { Id           = session.Id
      LastAccessed = session.LastAccessed
      Data         = session.Items
      }

  member this.ToSession () : IPersistableSession =
    upcast BasePersistableSession (this.Id, this.LastAccessed, this.Data)

// ---- Configuration and Store implementation ----

/// Configuration for RethinkDB session storage
[<AllowNullLiteral>]
type InMemorySessionConfiguration (cryptoConfig : CryptographyConfiguration) =
  inherit BasePersistableSessionConfiguration (cryptoConfig)

  /// Initializes a new instance of the <see cref="RethinkDbSessionConfiguration"/> class.
  new () = InMemorySessionConfiguration CryptographyConfiguration.Default

  /// The session store implementation
  override this.Store with get() = upcast InMemorySessionStore this


/// The InMemory session store
and InMemorySessionStore (cfg : InMemorySessionConfiguration) =
  
  /// Log access point
  let log = LogUtils<InMemorySessionStore> cfg.LogLevel

  /// You expected a dictionary?  Pfft....
  static let mutable sessions : StoredSession list = []

  interface IPersistableSessionStore with
    
    member __.SetUp () = ()

    member __.RetrieveSession id =
      log.dbug (fun () -> sprintf "Retrieving session Id %s" id)
      match sessions
            |> List.tryFind (fun sess -> id = string sess.Id) with
      | Some sess ->
          log.dbug (fun () -> sprintf "Found session Id %s" id)
          sess.ToSession ()
      | None ->
          log.dbug (fun () -> sprintf "Session Id %s not found" id)
          null

    member __.CreateNewSession () =
      let session =
        { Id           = Guid.NewGuid()
          LastAccessed = DateTime.Now
          Data         = Dictionary<string, obj>()
          }
      log.info (fun () -> sprintf "Creating new session with Id %s" (string session.Id))
      sessions <- session :: sessions
      session.ToSession ()
  
    member __.UpdateLastAccessed id =
      log.dbug (fun () -> sprintf "Updating last accessed for session Id %s" (string id))
      match sessions
            |> List.tryFind (fun sess -> id = sess.Id) with
      | Some sess ->
          sessions <-
            sessions
            |> List.filter (fun s -> id <> s.Id)
            |> List.append [ { sess with LastAccessed = DateTime.Now } ]
      | None -> ()

    member __.UpdateSession session =
      log.dbug (fun () -> sprintf "Updating session data for session Id %s" (string session.Id))
      sessions <-
        sessions
        |> List.filter (fun sess -> sess.Id <> session.Id)
        |> List.append [ StoredSession.From session ]

    member __.ExpireSessions () =
      log.dbug (fun () -> "Expiring sessions")
      sessions <-
        sessions
        |> List.filter (fun sess -> sess.LastAccessed >= DateTime.Now - cfg.Expiry)
