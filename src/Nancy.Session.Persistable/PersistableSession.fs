namespace Nancy.Session.Persistable

open Nancy
open Nancy.Cryptography
open Nancy.Session
open Newtonsoft.Json
open System.Collections.Generic
open System

/// Interface for a persistable session
[<AllowNullLiteral>]
type IPersistableSession = 
  inherit ISession

  /// The Id of the session
  abstract Id : Guid with get

  /// The date/time the session was last accessed
  abstract LastAccessed : DateTime with get

  /// The items in the session
  abstract Items : IDictionary<string, obj> with get

  /// Get a strongly-typed value from the session
  abstract Get<'T> : string -> 'T

  /// Get a strongly-typed value from the session, or the given value if it does not exist
  abstract GetOrDefault<'T> : string * 'T -> 'T


/// A base class for persistable sessions
type BasePersistableSession(id : Guid, lastAccessed : DateTime, items : IDictionary<string, obj>) =
  
  let mutable _hasChanged = false

  new() = BasePersistableSession(Guid.NewGuid(), DateTime.Now, Dictionary<string, obj>())

  /// The Id of the session
  member this.Id with get() = id

  /// Get or set the last access time
  member this.LastAccessed with get() = lastAccessed

  /// The data items in this session
  member this.Items with get() = items

  /// Indicate that the data in the session has changed
  member this.Changed () = _hasChanged <- true

  interface IEnumerable<KeyValuePair<string, obj>> with
    member this.GetEnumerator () = this.Items.GetEnumerator ()

  interface Collections.IEnumerable with
    member this.GetEnumerator () = upcast (this :> IEnumerable<KeyValuePair<string, obj>>).GetEnumerator ()

  interface ISession with
    
    member this.Count with get() = this.Items.Count
    
    member this.DeleteAll () =
      match this.Items.Count with
      | 0 -> ()
      | _ -> this.Changed ()
      this.Items.Clear ()
    
    member this.Delete key = match this.Items.Remove key with
                             | true -> this.Changed ()
                             | _    -> ()
    
    member this.Item
      with get(index) =
        match this.Items.ContainsKey index with
        | true -> this.Items.[index]
        | _    -> null
      and set index v =
        match (match this.[index] with
               | null -> obj()
               | item -> item) with
        | value when value.Equals(v) -> ()
        | _                          -> this.Items.[index] <- v
                                        this.Changed ()

    member this.HasChanged with get() = _hasChanged

  interface IPersistableSession with
    
    member this.Id with get() = this.Id
    member this.LastAccessed with get() = this.LastAccessed
    member this.Items with get() = this.Items

    member this.GetOrDefault<'T> (key, value) =
      match this.[key] with
      | null          -> value
      | :? 'T as item -> item
      | item          -> try JsonConvert.DeserializeObject<'T>(string item)
                          with | _ -> value

    member this.Get<'T> key = this.GetOrDefault (key, Unchecked.defaultof<'T>)

  // IPersistalbe members accessible without casting

  /// Get a strongly-typed value from the session
  abstract Get<'T> : string -> 'T
  default this.Get<'T> key = (this :> IPersistableSession).Get<'T> key

  /// Get a strongly-typed value from the session, or a default value if it does not exist
  abstract GetOrDefault<'T> : string * 'T -> 'T
  default this.GetOrDefault<'T> (key, value : 'T) = (this :> IPersistableSession).GetOrDefault (key, value)

  // ISession members accessible without casting

  /// Delete all items from the session
  abstract DeleteAll : unit -> unit
  default this.DeleteAll () = (this :> ISession).DeleteAll ()

  /// Delete a key from the session
  abstract Delete : string -> unit
  default this.Delete key   = (this :> ISession).Delete key

  /// Get or set an object from the session (see Get<'T> for strongly-typed item retrieval)
  abstract Item : string -> obj with get, set
  default this.Item
    with get(index) = (this :> ISession).[index]
    and set index v = (this :> ISession).[index] <- v

  /// The count of items in the session
  abstract Count : int with get
  default this.Count with get() = (this :> ISession).Count
  
  /// Whether the session has changed since it has been loaded
  abstract HasChanged : bool with get
  default this.HasChanged with get() = (this :> ISession).HasChanged
