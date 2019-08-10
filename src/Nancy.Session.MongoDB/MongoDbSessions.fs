namespace Nancy.Session.MongoDB

open MongoDB.Driver
open Nancy.Cryptography
open Nancy.Session.Persistable
open Newtonsoft.Json
open System
open System.Collections.Generic

/// POCO used to persist session data in the collection
[<AllowNullLiteral>]
type MongoSession (sessId : Guid, lastAccessed, data : IDictionary<string, obj>) =
  member val Id           = string sessId                    with get, set
  member val LastAccessed = lastAccessed                     with get, set
  member val Data         = JsonConvert.SerializeObject data with get, set
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

  /// Log access point
  let log = LogUtils<MongoDBSessionStore> cfg.LogLevel
  
  /// Shorthand to get the document collection
  let collection () = cfg.Client.GetDatabase(cfg.Database).GetCollection<MongoSession> cfg.Collection

  /// Shorthand to filter the collection by an Id
  let byId sessId = Builders<MongoSession>.Filter.Eq ((fun sess -> sess.Id), sessId)

  /// Forward-pipeable first-or-default call
  let firstOrDefault (cursor : IAsyncCursor<MongoSession>) = (cursor.FirstOrDefaultAsync >> await) ()

  interface IPersistableSessionStore with
    
    member __.SetUp () =
      IndexKeysDefinition<MongoSession>.op_Implicit "LastAccessed"
      |> (CreateIndexModel >> collection().Indexes.CreateOneAsync >> await >> ignore)

    member __.RetrieveSession sessId =
      log.dbug (fun () -> sprintf "Retrieving session Id %s" sessId)
      match (byId >> collection().FindAsync >> await >> firstOrDefault) sessId with
      | null ->
          log.dbug (fun () -> sprintf "Session Id %s not found" sessId)
          null
      | doc ->
          log.dbug (fun () -> sprintf "Found session Id %s" sessId)
          doc.ToSession ()

    member __.CreateNewSession () =
      let sessId = Guid.NewGuid ()
      log.info (fun () -> sprintf "Creating new session with Id %s" (string sessId))
      let doc = MongoSession (sessId, DateTime.Now, Dictionary<string, obj> ())
      (collection().InsertOneAsync >> await') doc
      doc.ToSession ()
  
    member __.UpdateLastAccessed sessId =
      log.dbug (fun () -> sprintf "Updating last accessed for session Id %s" (string sessId))
      collection().UpdateOneAsync
        ((string >> byId) sessId, Builders<MongoSession>.Update.Set ((fun sess -> sess.LastAccessed), DateTime.Now))
      |> (await >> ignore)

    member __.UpdateSession session =
      log.dbug (fun () -> sprintf "Updating session data for session Id %s" (string session.Id))
      collection().ReplaceOneAsync (
        (string >> byId) session.Id,
        MongoSession (
          session.Id, (match cfg.UseRollingSessions with true -> DateTime.Now | _ -> session.LastAccessed),
          session.Items))
      |> (await >> ignore)

    member __.ExpireSessions () =
      log.dbug (fun () -> "Expiring sessions")
      Builders<MongoSession>.Filter.Lt ((fun sess -> sess.LastAccessed), DateTime.Now - cfg.Expiry)
      |> (collection().DeleteManyAsync >> await >> ignore)
