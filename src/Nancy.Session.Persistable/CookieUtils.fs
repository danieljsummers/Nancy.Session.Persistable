/// Utilities to work with cookies (persistable sessions store Ids in cookies)
module Nancy.Session.Persistable.CookieUtils

open Nancy
open Nancy.Cookies
open Nancy.Cryptography
open Nancy.Helpers
open Nancy.Session
open System

/// <summary>
/// Set a cookie with the session Id
/// </summary>
/// <param name="cfg">The cookie configuration for the session</param>
/// <param name="id">The session Id to set</param>
/// <param name="response">The response to which the cookie will be added</param>
/// <returns>The response with the cookie added</returns>
let setCookie (cfg : CookieBasedSessionsConfiguration) id (response : Response) =
  let encData    = cfg.CryptographyConfiguration.EncryptionProvider.Encrypt id
  let hmacBytes  = cfg.CryptographyConfiguration.HmacProvider.GenerateHmac encData
  let cookieData = HttpUtility.UrlEncode(sprintf "%s%s" (Convert.ToBase64String hmacBytes) encData)
  let cookie     = NancyCookie(cfg.CookieName, cookieData, true)
  cookie.Domain <- cfg.Domain
  cookie.Path   <- cfg.Path
  response.WithCookie cookie

/// <summary>
/// Get the session Id from the cookie
/// </summary>
/// <param name="cfg">The cookie configuration for the session</param>
/// <param name="request">The request within which the cookie may be found</param>
/// <returns>The Id (or None if the cookie is not set or is invalid)</returns>
let getIdFromCookie (cfg : CookieBasedSessionsConfiguration) (request : Request) =
  match request.Cookies.ContainsKey cfg.CookieName with
  | true -> let hmacProvider = cfg.CryptographyConfiguration.HmacProvider
            let cookieData   = HttpUtility.UrlDecode request.Cookies.[cfg.CookieName]
            let hmacLength   = Base64Helpers.GetBase64Length hmacProvider.HmacLength
            match cookieData.Length >= hmacLength with
            | true -> let hmacString = cookieData.Substring (0, hmacLength)
                      let encCookie  = cookieData.Substring hmacLength
                      let hmacBytes  = Convert.FromBase64String hmacString
                      let newHmac    = hmacProvider.GenerateHmac encCookie
                      match HmacComparer.Compare (newHmac, hmacBytes, hmacProvider.HmacLength) with
                      | true -> Some <| cfg.CryptographyConfiguration.EncryptionProvider.Decrypt encCookie
                      | _    -> None
            | _    -> None
  | _    -> None
