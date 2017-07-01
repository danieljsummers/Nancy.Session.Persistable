[<AutoOpen>]
[<System.Runtime.CompilerServices.Extension>]
module Nancy.Session.Persistable.RequestExtensions

open Nancy

type Request with
  /// The current session as a persistable session
  member this.PersistableSession = this.Session :?> IPersistableSession

/// Get the session as a strongly-typed persistable session
[<System.Runtime.CompilerServices.Extension>]
let PersistableSession (x : Request) = x.PersistableSession
