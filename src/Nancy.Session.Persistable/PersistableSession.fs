namespace Nancy.Session.Persistable

open Nancy.Session
open Newtonsoft.Json
open System.Collections.Generic

/// Interface for a persistable session
type IPersistableSession = 
  inherit ISession

  /// Get a strongly-typed value from the session
  abstract Get<'T> : string -> 'T


/// A base class for persistable sessions
type BasePersistableSession(values : IDictionary<string, obj>) =
  inherit Session(values)

  new() = BasePersistableSession(Dictionary<string, obj>())

  interface IPersistableSession with
    member this.Get<'T> key = match this.[key] with
                              | null          -> Unchecked.defaultof<'T>
                              | :? 'T as item -> item
                              | item          -> try JsonConvert.DeserializeObject<'T>(string item)
                                                 with | _ -> Unchecked.defaultof<'T>

  /// <summary>
  /// Get a strongly-typed value from the session
  /// </summary>
  /// <param name="key">The key for the session value</param>
  /// <returns>The value (or a default of that type if the value is not set or does not deserialize)</returns>
  abstract Get<'T> : string -> 'T
  default this.Get<'T> key = (this :> IPersistableSession).Get<'T> key
