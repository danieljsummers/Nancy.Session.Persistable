namespace Nancy.Session.Persistable

open Nancy
open Nancy.Cryptography
open Nancy.Session
open System

/// Session log levels
type SessionLogLevel =
| None = 0
| Information = 1
| Debug = 2


/// Session store interface
type IPersistableSessionStore =
  
  abstract SetUp : unit -> unit

  /// Create a new session and return it
  abstract CreateNewSession : unit -> IPersistableSession

  /// Retrieve a session from the store
  abstract RetrieveSession : string -> IPersistableSession

  /// Update the last accessed date/time in the store
  abstract UpdateLastAccessed : Guid -> unit

  /// Update the session in the store
  abstract UpdateSession : IPersistableSession -> unit

  /// Remove expired sessions from the store
  abstract ExpireSessions : unit -> unit


/// Session configuration
and [<AllowNullLiteral>] IPersistableSessionConfiguration =
  
  /// The session store
  abstract Store : IPersistableSessionStore with get

  /// Configuration for the cookies which will hold the session Id
  abstract CookieConfiguration : CookieBasedSessionsConfiguration with get

  /// Reset session expiration on every request (true) or expire sessions based on their creation date (false)
  abstract UseRollingSessions : bool with get, set

  /// The time for which sessions are valid
  abstract Expiry : TimeSpan with get, set

  /// How frequently to check for expired sessions
  abstract ExpiryCheckFrequency : TimeSpan with get, set

  /// Whether to display debug information on the console
  abstract LogLevel : SessionLogLevel with get, set

  /// Whether the configuration is valid
  abstract IsValid : bool with get


/// Base configuration options for persistable sessions
[<AbstractClass>]
[<AllowNullLiteral>]
type BasePersistableSessionConfiguration (cryptoConfig : CryptographyConfiguration) as this =

  let mutable _useRollingSessions   = true
  let mutable _expiry               = TimeSpan (2, 0, 0)
  let mutable _expiryCheckFrequency = TimeSpan (0, 1, 0)
  let mutable _logLevel             = SessionLogLevel.None

  //do
  //  this.SetSerializer ()
  
  abstract Store : IPersistableSessionStore with get

  member __.CookieConfiguration = 
    let cfg = CookieBasedSessionsConfiguration cryptoConfig
    cfg.Serializer <- DefaultObjectSerializer ()
    cfg
  
  /// Gets or sets whether to use rolling sessions (expiry based on inactivity) or not (expiry based on creation)
  abstract UseRollingSessions : bool with get, set
  default __.UseRollingSessions with get () = _useRollingSessions and set v = _useRollingSessions <- v

  /// Gets or sets the session expiry period
  abstract Expiry : TimeSpan with get, set
  default __.Expiry with get () = _expiry and set v = _expiry <- v

  /// Gets or sets the frequency with which expired sessions are removed from storage
  abstract ExpiryCheckFrequency : TimeSpan with get, set
  default __.ExpiryCheckFrequency with get () = _expiryCheckFrequency and set v = _expiryCheckFrequency <- v

  /// Gets or sets the level of debug information to be displayed
  abstract LogLevel : SessionLogLevel with get, set
  default __.LogLevel with get () = _logLevel and set v = _logLevel <- v

  /// Get the validity state of the session
  abstract IsValid : bool with get
  default this.IsValid with get () = this.CookieConfiguration.IsValid

  //member private this.SetSerializer () = this.CookieConfiguration.Serializer <- DefaultObjectSerializer ()

  interface IPersistableSessionConfiguration with
    member this.Store                with get () = this.Store
    member this.CookieConfiguration  with get () = this.CookieConfiguration
    member this.UseRollingSessions   with get () = this.UseRollingSessions   and set v = this.UseRollingSessions <- v
    member this.Expiry               with get () = this.Expiry               and set v = this.Expiry <- v
    member this.ExpiryCheckFrequency with get () = this.ExpiryCheckFrequency and set v = this.ExpiryCheckFrequency <- v
    member this.LogLevel             with get () = this.LogLevel             and set v = this.LogLevel <- v
    member this.IsValid              with get () = this.IsValid


/// Logging utilities for use by session store implementations
type LogUtils<'T> (level) as this =
  
  let name = this.GetType().GenericTypeArguments.[0].Name

  /// Debug text; set SessionLogLevel.Debug to display
  member __.dbug (text : unit -> string) =
    match level with SessionLogLevel.Debug -> Console.WriteLine (sprintf "[%s] %s" name (text ())) | _ -> ()

  /// Info text; set SessionLogLevel.Information to display
  member __.info (text : unit -> string) =
    match level with
    | SessionLogLevel.Debug | SessionLogLevel.Information -> Console.WriteLine (sprintf "[%s] %s" name (text ()))
    | _ -> ()
