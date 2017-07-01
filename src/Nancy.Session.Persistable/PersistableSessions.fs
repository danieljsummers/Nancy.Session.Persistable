namespace Nancy.Session.Persistable

open Nancy
open Nancy.Bootstrapper
open Nancy.Cookies
open Nancy.Cryptography
open Nancy.Helpers
open System

/// Persistable sessions
type PersistableSessions(cfg : IPersistableSessionConfiguration) =

  do
    match cfg with
    | null -> nullArg "cfg"
    | _ -> match cfg.IsValid with true -> () | _ -> invalidArg "cfg" "configuration is invalid"
    cfg.Store.SetUp ()

  /// The date/time we last cleaned out expired sessions
  let mutable lastExpiryCheck = DateTime.MinValue

  /// The session store
  let store = cfg.Store

  /// Set a cookie with the session Id
  let setCookie (session : IPersistableSession) (response : Response) =
    let config     = cfg.CookieConfiguration
    let encData    = config.CryptographyConfiguration.EncryptionProvider.Encrypt (string session.Id)
    let hmacBytes  = config.CryptographyConfiguration.HmacProvider.GenerateHmac encData
    let cookieData = HttpUtility.UrlEncode (sprintf "%s%s" (Convert.ToBase64String hmacBytes) encData)
    let cookie     = NancyCookie (config.CookieName, cookieData, true)
    cookie.Domain  <- config.Domain
    cookie.Path    <- config.Path
    cookie.Expires <- Nullable <| session.LastAccessed + cfg.Expiry
    response.WithCookie cookie
    |> ignore

  /// Get the session Id from the cookie
  let getIdFromCookie (request : Request) =
    let config = cfg.CookieConfiguration
    match request.Cookies.ContainsKey config.CookieName with
    | true ->
        let hmacProvider = config.CryptographyConfiguration.HmacProvider
        let cookieData   = HttpUtility.UrlDecode request.Cookies.[config.CookieName]
        let hmacLength   = Base64Helpers.GetBase64Length hmacProvider.HmacLength
        match cookieData.Length >= hmacLength with
        | true ->
            let hmacString = cookieData.Substring (0, hmacLength)
            let encCookie  = cookieData.Substring hmacLength
            let hmacBytes  = Convert.FromBase64String hmacString
            let newHmac    = hmacProvider.GenerateHmac encCookie
            match HmacComparer.Compare (newHmac, hmacBytes, hmacProvider.HmacLength) with
            | true -> Some <| config.CryptographyConfiguration.EncryptionProvider.Decrypt encCookie
            | _ -> None
        | _ -> None
    | _ -> None

  /// Expire old sessions
  let expireOldSessions () =
    match cfg.ExpiryCheckFrequency >= DateTime.Now - lastExpiryCheck with
    | true -> ()
    | _ ->
        store.ExpireSessions ()
        lastExpiryCheck <- DateTime.Now

  /// <summary>
  /// Save the session into the response
  /// </summary>
  /// <param name="session">Session to save</param>
  /// <param name="response">Response to save into</param>
  member this.Save (session : IPersistableSession) (response : Nancy.Response) =
    expireOldSessions ()
    match isNull session || not session.HasChanged with true -> () | _ -> store.UpdateSession session
    setCookie session response

  /// <summary>
  /// Loads the session from the request
  /// </summary>
  /// <param name="request">Request to load from</param>
  /// <returns>ISession containing the load session values</returns>
  member this.Load (request : Request) =
    expireOldSessions ()
    match getIdFromCookie request with
    | Some id ->
        match store.RetrieveSession id with
        | null -> store.CreateNewSession ()
        | sess ->
            match cfg.UseRollingSessions with true -> store.UpdateLastAccessed sess.Id | _ -> ()
            sess
    | _ -> store.CreateNewSession ()

  /// <summary>
  /// Saves the request session into the response
  /// </summary>
  /// <param name="ctx">Nancy context</param>
  /// <param name="provider">Persistable session provider instance</param>
  static member private SaveSession (ctx : NancyContext) (provider : PersistableSessions) =
    provider.Save ctx.Request.PersistableSession ctx.Response

  /// <summary>
  /// Loads the request session
  /// </summary>
  /// <param name="ctx">Nancy context</param>
  /// <param name="store">RethinkDB session store instance</param>
  /// <returns>Always returns null, as a non-null return indicates a change in the pipeline</returns>
  static member private LoadSession (ctx : NancyContext) (provider : PersistableSessions) : Nancy.Response =
    match ctx.Request with null -> () | _ -> ctx.Request.Session <- provider.Load ctx.Request
    null

  /// <summary>
  /// Initialise and add RethinkDB session storage hooks to the application pipeline
  /// </summary>
  /// <param name="pipelines">Application pipelines</param>
  /// <param name="cfg">RethinkDB session store configuration</param>
  static member Enable (pipelines : IPipelines, cfg) =
    match pipelines with
    | null -> nullArg "pipelines"
    | _ ->
        let provider = PersistableSessions cfg
        pipelines.BeforeRequest.AddItemToStartOfPipeline (fun ctx -> PersistableSessions.LoadSession ctx provider)
        pipelines.AfterRequest.AddItemToEndOfPipeline    (fun ctx -> PersistableSessions.SaveSession ctx provider)
