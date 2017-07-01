namespace Nancy.Session.MongoDB

open MongoDB.Driver
open Nancy.Cryptography
open Nancy.Session.Persistable
open Newtonsoft.Json
open System
open System.Collections.Generic

/// POCO used to persist session data in the collection
[<AllowNullLiteral>]
type MongoSession() =
  member val Id           = ""         with get, set
  member val LastAccessed = DateTime() with get, set
  member val Data         = ""         with get, set
  member this.ToSession () : IPersistableSession =
    upcast BasePersistableSession
      (Guid.Parse this.Id, this.LastAccessed, JsonConvert.DeserializeObject<Dictionary<string, obj>> this.Data)


/// Configuration for the MongoDB session store
[<AllowNullLiteral>]
type MongoDBSessionConfiguration (client : MongoClient, cryptoConfig) =
  inherit BasePersistableSessionConfiguration (cryptoConfig)

  new (client) = MongoDBSessionConfiguration (client, CryptographyConfiguration.Default)
  
  new () = MongoDBSessionConfiguration (null, CryptographyConfiguration.Default)

  /// The MongoDB client instance to use for data access
  member val Client = client with get, set

  /// The name of the database in which sessions will be stored
  member val Database = "NancySession" with get, set

  /// The name of the document collection in which sessions will be stored
  member val Collection = "Session" with get, set

  /// The MongoDB session store implementation
  override this.Store with get () = upcast MongoDBSessionStore this

/// Implementation of a session store for MongoDB
and MongoDBSessionStore (cfg : MongoDBSessionConfiguration) =
  
  let await  task = task |> (Async.AwaitTask >> Async.RunSynchronously)
  let await' task = task |> (Async.AwaitIAsyncResult >> Async.Ignore >> Async.RunSynchronously)

  /// Debug text - may be removed before 1.0
  let dbg (text : unit -> string) =
#if DEBUG
    System.Console.WriteLine (sprintf "[MongoSession] %s" (text ()))
#endif
    ()
  
  /// Shorthand to get the document collection
  let collection () = cfg.Client.GetDatabase(cfg.Database).GetCollection<MongoSession> cfg.Collection

  /// Shorthand to filter the collection by an Id
  let byId id = Builders<MongoSession>.Filter.Eq ((fun sess -> sess.Id), id)

  /// Forward-pipeable first-or-default call
  let firstOrDefault (cursor : IAsyncCursor<MongoSession>) = cursor.FirstOrDefaultAsync () |> await

  interface IPersistableSessionStore with
    
    member __.SetUp () =
      collection().Indexes.CreateOneAsync
        (Builders<MongoSession>.IndexKeys.Ascending(FieldDefinition<MongoSession>.op_Implicit "LastAccessed"))
      |> (await >> ignore)

    member __.RetrieveSession id =
      dbg (fun () -> sprintf "Retrieving session Id %s" id)
      match collection().FindAsync (byId id)
            |> await
            |> firstOrDefault with
      | null ->
          dbg (fun () -> sprintf "Session Id %s not found" id)
          null
      | doc ->
          dbg (fun () -> sprintf "Found session Id %s" id)
          doc.ToSession ()

    member __.CreateNewSession () =
      let id = string (Guid.NewGuid ())
      dbg (fun () -> sprintf "Creating new session with Id %s" id)
      let doc =
        MongoSession(
          Id           = id,
          LastAccessed = DateTime.Now,
          Data         = JsonConvert.SerializeObject (Dictionary<string, obj> ()))
      collection().InsertOneAsync doc
      |> await'
      doc.ToSession ()
  
    member __.UpdateLastAccessed id =
      dbg (fun () -> sprintf "Updating last accessed for session Id %s" (string id))
      collection().UpdateOneAsync
        ((byId <| string id), Builders<MongoSession>.Update.Set ((fun sess -> sess.LastAccessed), DateTime.Now))
      |> (await >> ignore)

    member __.UpdateSession session =
      dbg (fun () -> sprintf "Updating session data for session Id %s" (string session.Id))
      collection().ReplaceOneAsync
        ((byId <| string session.Id),
         MongoSession(
           Id           = string session.Id,
           LastAccessed = (match cfg.UseRollingSessions with true -> DateTime.Now | _ -> session.LastAccessed),
           Data         = JsonConvert.SerializeObject session.Items))
      |> (await >> ignore)

    member __.ExpireSessions () =
      dbg (fun () -> "Expiring sessions")
      collection().DeleteManyAsync
        (Builders<MongoSession>.Filter.Lt ((fun sess -> sess.LastAccessed), DateTime.Now - cfg.Expiry))
      |> (await >> ignore)
