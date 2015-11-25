/*
 * Licensed under the Apache License, Version 2.0 (http://www.apache.org/licenses/LICENSE-2.0)
 * See https://github.com/aspnet-contrib/AspNet.Security.OpenIdConnect.Server
 * for more information concerning the license and the contributors participating to this project.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IdentityModel.Tokens;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Caching;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Protocols;
using Microsoft.Owin.Logging;
using Microsoft.Owin.Security;
using Microsoft.Owin.Security.Infrastructure;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Owin.Security.OpenIdConnect.Extensions;

namespace Owin.Security.OpenIdConnect.Server {
    internal partial class OpenIdConnectServerHandler : AuthenticationHandler<OpenIdConnectServerOptions> {
        private async Task<bool> InvokeAuthorizationEndpointAsync() {
            OpenIdConnectMessage request;

            if (string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                // Create a new authorization request using the
                // parameters retrieved from the query string.
                request = new OpenIdConnectMessage(Request.Query) {
                    RequestType = OpenIdConnectRequestType.AuthenticationRequest
                };
            }

            else if (string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
                // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
                if (string.IsNullOrEmpty(Request.ContentType)) {
                    Options.Logger.WriteInformation("A malformed request has been received by the authorization endpoint.");

                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed authorization request has been received: " +
                            "the mandatory 'Content-Type' header was missing from the POST request."
                    });
                }

                // May have media/type; charset=utf-8, allow partial match.
                if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) {
                    Options.Logger.WriteInformation("A malformed request has been received by the authorization endpoint.");

                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed authorization request has been received: " +
                            "the 'Content-Type' header contained an unexcepted value. " +
                            "Make sure to use 'application/x-www-form-urlencoded'."
                    });
                }

                // Create a new authorization request using the
                // parameters retrieved from the request form.
                request = new OpenIdConnectMessage(await Request.ReadFormAsync()) {
                    RequestType = OpenIdConnectRequestType.AuthenticationRequest
                };
            }

            else {
                Options.Logger.WriteInformation("A malformed request has been received by the authorization endpoint.");

                return await SendErrorPageAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed authorization request has been received: " +
                        "make sure to use either GET or POST."
                });
            }

            // Re-assemble the authorization request using the cache if
            // a 'unique_id' parameter has been extracted from the received message.
            var identifier = request.GetUniqueIdentifier();
            if (!string.IsNullOrEmpty(identifier)) {
                var item = Options.Cache.Get(identifier) as string;
                if (item == null) {
                    Options.Logger.WriteInformation("A unique_id has been provided but no corresponding " +
                                            "OpenID Connect request has been found in the cache.");

                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "Invalid request: timeout expired."
                    });
                }

                using (var stream = new MemoryStream(Convert.FromBase64String(item)))
                using (var reader = new BinaryReader(stream)) {
                    // Make sure the stored authorization request
                    // has been serialized using the same method.
                    var version = reader.ReadInt32();
                    if (version != 1) {
                        Options.Cache.Remove(identifier);

                        Options.Logger.WriteError("An invalid OpenID Connect request has been found in the cache.");

                        return await SendErrorPageAsync(new OpenIdConnectMessage {
                            Error = OpenIdConnectConstants.Errors.InvalidRequest,
                            ErrorDescription = "Invalid request: timeout expired."
                        });
                    }

                    for (int index = 0, length = reader.ReadInt32(); index < length; index++) {
                        var name = reader.ReadString();
                        var value = reader.ReadString();

                        // Skip restoring the parameter retrieved from the stored request
                        // if the OpenID Connect message extracted from the query string
                        // or the request form defined the same parameter.
                        if (!request.Parameters.ContainsKey(name)) {
                            request.SetParameter(name, value);
                        }
                    }
                }
            }
            
            // Store the authorization request in the OWIN context.
            Context.SetOpenIdConnectRequest(request);

            // client_id is mandatory parameter and MUST cause an error when missing.
            // See http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
            if (string.IsNullOrEmpty(request.ClientId)) {
                return await SendErrorPageAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "client_id was missing"
                });
            }

            // While redirect_uri was not mandatory in OAuth2, this parameter
            // is now declared as REQUIRED and MUST cause an error when missing.
            // See http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
            // To keep AspNet.Security.OpenIdConnect.Server compatible with pure OAuth2 clients,
            // an error is only returned if the request was made by an OpenID Connect client.
            if (string.IsNullOrEmpty(request.RedirectUri) && request.ContainsScope(OpenIdConnectConstants.Scopes.OpenId)) {
                return await SendErrorPageAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "redirect_uri must be included when making an OpenID Connect request"
                });
            }

            if (!string.IsNullOrEmpty(request.RedirectUri)) {
                Uri uri;
                if (!Uri.TryCreate(request.RedirectUri, UriKind.Absolute, out uri)) {
                    // redirect_uri MUST be an absolute URI.
                    // See http://tools.ietf.org/html/rfc6749#section-3.1.2
                    // and http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "redirect_uri must be absolute"
                    });
                }

                else if (!string.IsNullOrEmpty(uri.Fragment)) {
                    // redirect_uri MUST NOT include a fragment component.
                    // See http://tools.ietf.org/html/rfc6749#section-3.1.2
                    // and http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "redirect_uri must not include a fragment"
                    });
                }

                else if (!Options.AllowInsecureHttp && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) {
                    // redirect_uri SHOULD require the use of TLS
                    // http://tools.ietf.org/html/rfc6749#section-3.1.2.1
                    // and http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest
                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "redirect_uri does not meet the security requirements"
                    });
                }
            }

            var clientNotification = new ValidateClientRedirectUriContext(Context, Options, request);
            await Options.Provider.ValidateClientRedirectUri(clientNotification);

            // Reject the authorization request if the redirect_uri was not validated.
            if (!clientNotification.IsValidated) {
                Options.Logger.WriteVerbose("Unable to validate client information");

                return await SendErrorPageAsync(new OpenIdConnectMessage {
                    Error = clientNotification.Error ?? OpenIdConnectConstants.Errors.InvalidClient,
                    ErrorDescription = clientNotification.ErrorDescription,
                    ErrorUri = clientNotification.ErrorUri
                });
            }

            if (!string.IsNullOrEmpty(request.GetParameter(OpenIdConnectConstants.Parameters.Request))) {
                Options.Logger.WriteVerbose("The authorization request contained the unsupported request parameter.");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.RequestNotSupported,
                    ErrorDescription = "The request parameter is not supported.",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            else if (!string.IsNullOrEmpty(request.RequestUri)) {
                Options.Logger.WriteVerbose("The authorization request contained the unsupported request_uri parameter.");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.RequestUriNotSupported,
                    ErrorDescription = "The request_uri parameter is not supported.",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            else if (string.IsNullOrEmpty(request.ResponseType)) {
                Options.Logger.WriteVerbose("Authorization request missing required response_type parameter");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "response_type parameter missing",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            // Reject requests whose flow is unsupported.
            else if (!request.IsNoneFlow() && !request.IsAuthorizationCodeFlow() &&
                     !request.IsImplicitFlow() && !request.IsHybridFlow()) {
                Options.Logger.WriteVerbose("Authorization request contains unsupported response_type parameter");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.UnsupportedResponseType,
                    ErrorDescription = "response_type unsupported",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            // Reject requests whose response_mode is unsupported.
            else if (!request.IsFormPostResponseMode() && !request.IsFragmentResponseMode() && !request.IsQueryResponseMode()) {
                Options.Logger.WriteVerbose("Authorization request contains unsupported response_mode parameter");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "response_mode unsupported",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            // response_mode=query (explicit or not) and a response_type containing id_token
            // or token are not considered as a safe combination and MUST be rejected.
            // See http://openid.net/specs/oauth-v2-multiple-response-types-1_0.html#Security
            else if (request.IsQueryResponseMode() && (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.IdToken) ||
                                                       request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.Token))) {
                Options.Logger.WriteVerbose("Authorization request contains unsafe response_type/response_mode combination");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "response_type/response_mode combination unsupported",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            // Reject OpenID Connect implicit/hybrid requests missing the mandatory nonce parameter.
            // See http://openid.net/specs/openid-connect-core-1_0.html#AuthRequest,
            // http://openid.net/specs/openid-connect-implicit-1_0.html#RequestParameters
            // and http://openid.net/specs/openid-connect-core-1_0.html#HybridIDToken.
            else if (string.IsNullOrEmpty(request.Nonce) && request.ContainsScope(OpenIdConnectConstants.Scopes.OpenId) &&
                                                           (request.IsImplicitFlow() || request.IsHybridFlow())) {
                Options.Logger.WriteVerbose("The 'nonce' parameter was missing");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "nonce parameter missing",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            // Reject requests containing the id_token response_mode if no openid scope has been received.
            else if (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.IdToken) &&
                    !request.ContainsScope(OpenIdConnectConstants.Scopes.OpenId)) {
                Options.Logger.WriteVerbose("The 'openid' scope part was missing");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "openid scope missing",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            // Reject requests containing the code response_mode if the token endpoint has been disabled.
            else if (request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.Code) &&
                    !Options.TokenEndpointPath.HasValue) {
                Options.Logger.WriteVerbose("Authorization request contains the disabled code response_type");

                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.UnsupportedResponseType,
                    ErrorDescription = "response_type=code is not supported by this server",
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            var validationNotification = new ValidateAuthorizationRequestContext(Context, Options, request);
            await Options.Provider.ValidateAuthorizationRequest(validationNotification);

            // Stop processing the request if Validated was not called.
            if (!validationNotification.IsValidated) {
                return await SendErrorRedirectAsync(request, new OpenIdConnectMessage {
                    Error = validationNotification.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = validationNotification.ErrorDescription,
                    ErrorUri = validationNotification.ErrorUri,
                    RedirectUri = request.RedirectUri,
                    State = request.State
                });
            }

            identifier = request.GetUniqueIdentifier();
            if (string.IsNullOrEmpty(identifier)) {
                // Generate a new 256-bits identifier and associate it with the authorization request.
                identifier = GenerateKey(length: 256 / 8);
                request.SetUniqueIdentifier(identifier);

                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream)) {
                    writer.Write(/* version: */ 1);
                    writer.Write(request.Parameters.Count);

                    foreach (var parameter in request.Parameters) {
                        writer.Write(parameter.Key);
                        writer.Write(parameter.Value);
                    }

                    // Store the authorization request in the cache.
                    Options.Cache.Add(identifier, Convert.ToBase64String(stream.ToArray()), new CacheItemPolicy {
                        SlidingExpiration = TimeSpan.FromHours(1)
                    });
                }
            }

            var notification = new AuthorizationEndpointContext(Context, Options, request);
            await Options.Provider.AuthorizationEndpoint(notification);

            if (notification.HandledResponse) {
                return true;
            }

            return false;
        }

        private async Task InvokeConfigurationEndpointAsync() {
            var notification = new ConfigurationEndpointContext(Context, Options);
            notification.Issuer = Context.GetIssuer(Options);

            // Metadata requests must be made via GET.
            // See http://openid.net/specs/openid-connect-discovery-1_0.html#ProviderConfigurationRequest
            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                Options.Logger.WriteError("Configuration endpoint: invalid method used.");

                return;
            }

            if (Options.AuthorizationEndpointPath.HasValue) {
                notification.AuthorizationEndpoint = notification.Issuer.AddPath(Options.AuthorizationEndpointPath);
            }

            if (Options.CryptographyEndpointPath.HasValue) {
                notification.CryptographyEndpoint = notification.Issuer.AddPath(Options.CryptographyEndpointPath);
            }

            if (Options.ProfileEndpointPath.HasValue) {
                notification.ProfileEndpoint = notification.Issuer.AddPath(Options.ProfileEndpointPath);
            }

            if (Options.ValidationEndpointPath.HasValue) {
                notification.ValidationEndpoint = notification.Issuer.AddPath(Options.ValidationEndpointPath);
            }

            if (Options.TokenEndpointPath.HasValue) {
                notification.TokenEndpoint = notification.Issuer.AddPath(Options.TokenEndpointPath);
            }

            if (Options.LogoutEndpointPath.HasValue) {
                notification.LogoutEndpoint = notification.Issuer.AddPath(Options.LogoutEndpointPath);
            }

            if (Options.AuthorizationEndpointPath.HasValue) {
                // Only expose the implicit grant type if the token
                // endpoint has not been explicitly disabled.
                notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.Implicit);

                if (Options.TokenEndpointPath.HasValue) {
                    // Only expose the authorization code and refresh token grant types
                    // if both the authorization and the token endpoints are enabled.
                    notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.AuthorizationCode);
                }
            }

            if (Options.TokenEndpointPath.HasValue) {
                notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.RefreshToken);

                // If the authorization endpoint is disabled, assume the authorization server will
                // allow the client credentials and resource owner password credentials grant types.
                if (!Options.AuthorizationEndpointPath.HasValue) {
                    notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.ClientCredentials);
                    notification.GrantTypes.Add(OpenIdConnectConstants.GrantTypes.Password);
                }
            }

            // Only populate response_modes_supported and response_types_supported
            // if the authorization endpoint is available.
            if (Options.AuthorizationEndpointPath.HasValue) {
                notification.ResponseModes.Add(OpenIdConnectConstants.ResponseModes.FormPost);
                notification.ResponseModes.Add(OpenIdConnectConstants.ResponseModes.Fragment);
                notification.ResponseModes.Add(OpenIdConnectConstants.ResponseModes.Query);

                notification.ResponseTypes.Add(OpenIdConnectConstants.ResponseTypes.Token);
                notification.ResponseTypes.Add(OpenIdConnectConstants.ResponseTypes.IdToken);

                notification.ResponseTypes.Add(
                    OpenIdConnectConstants.ResponseTypes.IdToken + ' ' +
                    OpenIdConnectConstants.ResponseTypes.Token);

                // Only expose response types containing code when
                // the token endpoint has not been explicitly disabled.
                if (Options.TokenEndpointPath.HasValue) {
                    notification.ResponseTypes.Add(OpenIdConnectConstants.ResponseTypes.Code);

                    notification.ResponseTypes.Add(
                        OpenIdConnectConstants.ResponseTypes.Code + ' ' +
                        OpenIdConnectConstants.ResponseTypes.Token);

                    notification.ResponseTypes.Add(
                        OpenIdConnectConstants.ResponseTypes.Code + ' ' +
                        OpenIdConnectConstants.ResponseTypes.IdToken);

                    notification.ResponseTypes.Add(
                        OpenIdConnectConstants.ResponseTypes.Code + ' ' +
                        OpenIdConnectConstants.ResponseTypes.IdToken + ' ' +
                        OpenIdConnectConstants.ResponseTypes.Token);
                }
            }

            notification.Scopes.Add(OpenIdConnectConstants.Scopes.OpenId);

            notification.SubjectTypes.Add(OpenIdConnectConstants.SubjectTypes.Public);

            notification.SigningAlgorithms.Add(OpenIdConnectConstants.Algorithms.RS256);

            await Options.Provider.ConfigurationEndpoint(notification);

            if (notification.HandledResponse) {
                return;
            }
            
            var payload = new JObject();

            payload.Add(OpenIdConnectConstants.Metadata.Issuer, notification.Issuer);

            if (!string.IsNullOrEmpty(notification.AuthorizationEndpoint)) {
                payload.Add(OpenIdConnectConstants.Metadata.AuthorizationEndpoint, notification.AuthorizationEndpoint);
            }

            if (!string.IsNullOrEmpty(notification.ProfileEndpoint)) {
                payload.Add(OpenIdConnectConstants.Metadata.UserinfoEndpoint, notification.ProfileEndpoint);
            }

            if (!string.IsNullOrWhiteSpace(notification.ValidationEndpoint)) {
                payload.Add(OpenIdConnectConstants.Metadata.IntrospectionEndpoint, notification.ValidationEndpoint);
            }

            if (!string.IsNullOrEmpty(notification.TokenEndpoint)) {
                payload.Add(OpenIdConnectConstants.Metadata.TokenEndpoint, notification.TokenEndpoint);
            }

            if (!string.IsNullOrEmpty(notification.LogoutEndpoint)) {
                payload.Add(OpenIdConnectConstants.Metadata.EndSessionEndpoint, notification.LogoutEndpoint);
            }

            if (!string.IsNullOrEmpty(notification.CryptographyEndpoint)) {
                payload.Add(OpenIdConnectConstants.Metadata.JwksUri, notification.CryptographyEndpoint);
            }

            payload.Add(OpenIdConnectConstants.Metadata.GrantTypesSupported,
                JArray.FromObject(notification.GrantTypes.Distinct()));

            payload.Add(OpenIdConnectConstants.Metadata.ResponseModesSupported,
                JArray.FromObject(notification.ResponseModes.Distinct()));

            payload.Add(OpenIdConnectConstants.Metadata.ResponseTypesSupported,
                JArray.FromObject(notification.ResponseTypes.Distinct()));

            payload.Add(OpenIdConnectConstants.Metadata.SubjectTypesSupported,
                JArray.FromObject(notification.SubjectTypes.Distinct()));

            payload.Add(OpenIdConnectConstants.Metadata.ScopesSupported,
                JArray.FromObject(notification.Scopes.Distinct()));

            payload.Add(OpenIdConnectConstants.Metadata.IdTokenSigningAlgValuesSupported,
                JArray.FromObject(notification.SigningAlgorithms.Distinct()));

            var context = new ConfigurationEndpointResponseContext(Context, Options, payload);
            await Options.Provider.ConfigurationEndpointResponse(context);

            if (context.HandledResponse) {
                return;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer))) {
                payload.WriteTo(writer);
                writer.Flush();

                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Request.CallCancelled);
            }
        }

        private async Task InvokeCryptographyEndpointAsync() {
            var notification = new CryptographyEndpointContext(Context, Options);

            // Metadata requests must be made via GET.
            // See http://openid.net/specs/openid-connect-discovery-1_0.html#ProviderConfigurationRequest
            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                Options.Logger.WriteError("Cryptography endpoint: invalid method used.");

                return;
            }

            foreach (var credentials in Options.EncryptingCredentials) {
                // Ignore the key if it's not supported.
                if (!credentials.SecurityKey.IsSupportedAlgorithm(SecurityAlgorithms.RsaOaepKeyWrap) &&
                    !credentials.SecurityKey.IsSupportedAlgorithm(SecurityAlgorithms.RsaV15KeyWrap)) {
                    Options.Logger.WriteVerbose("Cryptography endpoint: unsupported encryption key ignored. " +
                                                "Only asymmetric security keys supporting RSA1_5 or RSA-OAEP " +
                                                "can be exposed via the JWKS endpoint.");

                    continue;
                }

                X509Certificate2 x509Certificate = null;

                // Determine whether the encrypting credentials are directly based on a X.509 certificate.
                var x509EncryptingCredentials = credentials as X509EncryptingCredentials;
                if (x509EncryptingCredentials != null) {
                    x509Certificate = x509EncryptingCredentials.Certificate;
                }

                // Skip looking for a X509SecurityKey in EncryptingCredentials.SecurityKey
                // if a certificate has been found in the EncryptingCredentials instance.
                if (x509Certificate == null) {
                    // Determine whether the security key is an asymmetric key embedded in a X.509 certificate.
                    var x509SecurityKey = credentials.SecurityKey as X509SecurityKey;
                    if (x509SecurityKey != null) {
                        x509Certificate = x509SecurityKey.Certificate;
                    }
                }

                // Skip looking for a X509AsymmetricSecurityKey in EncryptingCredentials.SecurityKey
                // if a certificate has been found in EncryptingCredentials or EncryptingCredentials.SecurityKey.
                if (x509Certificate == null) {
                    // Determine whether the security key is an asymmetric key embedded in a X.509 certificate.
                    var x509AsymmetricSecurityKey = credentials.SecurityKey as X509AsymmetricSecurityKey;
                    if (x509AsymmetricSecurityKey != null) {
                        // The X.509 certificate is not directly accessible when using X509AsymmetricSecurityKey.
                        // Reflection is the only way to get the certificate used to create the security key.
                        var field = typeof(X509AsymmetricSecurityKey).GetField(
                            name: "certificate",
                            bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic);
                        Debug.Assert(field != null);

                        x509Certificate = (X509Certificate2) field.GetValue(x509AsymmetricSecurityKey);
                    }
                }

                if (x509Certificate != null) {
                    // Create a new JSON Web Key exposing the
                    // certificate instead of its public RSA key.
                    notification.Keys.Add(new JsonWebKey {
                        Use = JsonWebKeyUseNames.Enc,
                        Kty = JsonWebAlgorithmsKeyTypes.RSA,

                        // Resolve the JWA identifier from the algorithm specified in the credentials.
                        Alg = OpenIdConnectServerHelpers.GetJwtAlgorithm(credentials.Algorithm),

                        // By default, use the hexadecimal representation of the
                        // certificate's SHA-1 hash as the unique key identifier.
                        Kid = x509Certificate.Thumbprint,

                        // x5t must be base64url-encoded.
                        // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#section-4.8
                        X5t = Base64UrlEncoder.Encode(x509Certificate.GetCertHash()),

                        // Unlike E or N, the certificates contained in x5c
                        // must be base64-encoded and not base64url-encoded.
                        // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#section-4.7
                        X5c = { Convert.ToBase64String(x509Certificate.RawData) }
                    });
                }

                else {
                    var key = (AsymmetricSecurityKey) credentials.SecurityKey;

                    // Resolve the underlying algorithm from the security key.
                    var algorithm = (RSA) key.GetAsymmetricAlgorithm(
                        algorithm: SecurityAlgorithms.RsaOaepKeyWrap,
                        privateKey: false);
                    Debug.Assert(algorithm != null);

                    // Export the RSA public key to create a new JSON Web Key
                    // exposing the exponent and the modulus parameters.
                    var parameters = algorithm.ExportParameters(includePrivateParameters: false);

                    notification.Keys.Add(new JsonWebKey {
                        Use = JsonWebKeyUseNames.Enc,
                        Kty = JsonWebAlgorithmsKeyTypes.RSA,

                        // Resolve the JWA identifier from the algorithm specified in the credentials.
                        Alg = OpenIdConnectServerHelpers.GetJwtAlgorithm(credentials.Algorithm),

                        // Create a unique identifier using the base64url-encoded representation of the modulus.
                        // Note: use the first 40 chars to avoid using a too long identifier.
                        Kid = Base64UrlEncoder.Encode(parameters.Modulus)
                                              .Substring(0, 40)
                                              .ToUpperInvariant(),

                        // Both E and N must be base64url-encoded.
                        // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#appendix-A.1
                        E = Base64UrlEncoder.Encode(parameters.Exponent),
                        N = Base64UrlEncoder.Encode(parameters.Modulus)
                    });
                }
            }

            foreach (var credentials in Options.SigningCredentials) {
                // Ignore the key if it's not supported.
                if (!credentials.SigningKey.IsSupportedAlgorithm(SecurityAlgorithms.RsaSha256Signature)) {
                    Options.Logger.WriteVerbose("Cryptography endpoint: unsupported signing key ignored. " +
                                                "Only asymmetric security keys supporting RS256, RS384 " +
                                                "or RS512 can be exposed via the JWKS endpoint.");

                    continue;
                }

                X509Certificate2 x509Certificate = null;

                // Determine whether the signing credentials are directly based on a X.509 certificate.
                var x509SigningCredentials = credentials as X509SigningCredentials;
                if (x509SigningCredentials != null) {
                    x509Certificate = x509SigningCredentials.Certificate;
                }

                // Skip looking for a X509SecurityKey in SigningCredentials.SigningKey
                // if a certificate has been found in the SigningCredentials instance.
                if (x509Certificate == null) {
                    // Determine whether the security key is an asymmetric key embedded in a X.509 certificate.
                    var x509SecurityKey = credentials.SigningKey as X509SecurityKey;
                    if (x509SecurityKey != null) {
                        x509Certificate = x509SecurityKey.Certificate;
                    }
                }

                // Skip looking for a X509AsymmetricSecurityKey in SigningCredentials.SigningKey
                // if a certificate has been found in SigningCredentials or SigningCredentials.SigningKey.
                if (x509Certificate == null) {
                    // Determine whether the security key is an asymmetric key embedded in a X.509 certificate.
                    var x509AsymmetricSecurityKey = credentials.SigningKey as X509AsymmetricSecurityKey;
                    if (x509AsymmetricSecurityKey != null) {
                        // The X.509 certificate is not directly accessible when using X509AsymmetricSecurityKey.
                        // Reflection is the only way to get the certificate used to create the security key.
                        var field = typeof(X509AsymmetricSecurityKey).GetField(
                            name: "certificate",
                            bindingAttr: BindingFlags.Instance | BindingFlags.NonPublic);
                        Debug.Assert(field != null);

                        x509Certificate = (X509Certificate2) field.GetValue(x509AsymmetricSecurityKey);
                    }
                }

                if (x509Certificate != null) {
                    // Create a new JSON Web Key exposing the
                    // certificate instead of its public RSA key.
                    notification.Keys.Add(new JsonWebKey {
                        Use = JsonWebKeyUseNames.Sig,
                        Kty = JsonWebAlgorithmsKeyTypes.RSA,

                        // Resolve the JWA identifier from the algorithm specified in the credentials.
                        Alg = OpenIdConnectServerHelpers.GetJwtAlgorithm(credentials.SignatureAlgorithm),

                        // By default, use the hexadecimal representation of the
                        // certificate's SHA-1 hash as the unique key identifier.
                        Kid = x509Certificate.Thumbprint,

                        // x5t must be base64url-encoded.
                        // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#section-4.8
                        X5t = Base64UrlEncoder.Encode(x509Certificate.GetCertHash()),

                        // Unlike E or N, the certificates contained in x5c
                        // must be base64-encoded and not base64url-encoded.
                        // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#section-4.7
                        X5c = { Convert.ToBase64String(x509Certificate.RawData) }
                    });
                }

                else {
                    var key = (AsymmetricSecurityKey) credentials.SigningKey;

                    // Resolve the underlying algorithm from the security key.
                    var algorithm = (RSA) key.GetAsymmetricAlgorithm(
                        algorithm: SecurityAlgorithms.RsaOaepKeyWrap,
                        privateKey: false);
                    Debug.Assert(algorithm != null);

                    // Export the RSA public key to create a new JSON Web Key
                    // exposing the exponent and the modulus parameters.
                    var parameters = algorithm.ExportParameters(includePrivateParameters: false);

                    notification.Keys.Add(new JsonWebKey {
                        Use = JsonWebKeyUseNames.Sig,
                        Kty = JsonWebAlgorithmsKeyTypes.RSA,

                        // Resolve the JWA identifier from the algorithm specified in the credentials.
                        Alg = OpenIdConnectServerHelpers.GetJwtAlgorithm(credentials.SignatureAlgorithm),

                        // Create a unique identifier using the base64url-encoded representation of the modulus.
                        // Note: use the first 40 chars to avoid using a too long identifier.
                        Kid = Base64UrlEncoder.Encode(parameters.Modulus)
                                              .Substring(0, 40)
                                              .ToUpperInvariant(),

                        // Both E and N must be base64url-encoded.
                        // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#appendix-A.1
                        E = Base64UrlEncoder.Encode(parameters.Exponent),
                        N = Base64UrlEncoder.Encode(parameters.Modulus)
                    });
                }
            }

            await Options.Provider.CryptographyEndpoint(notification);

            if (notification.HandledResponse) {
                return;
            }

            var payload = new JObject();
            var keys = new JArray();

            foreach (var key in notification.Keys) {
                var item = new JObject();

                // Ensure a key type has been provided.
                // See http://tools.ietf.org/html/draft-ietf-jose-json-web-key-31#section-4.1
                if (string.IsNullOrEmpty(key.Kty)) {
                    Options.Logger.WriteWarning("Cryptography endpoint: a JSON Web Key didn't " +
                        "contain the mandatory 'Kty' parameter and has been ignored.");

                    continue;
                }

                // Create a dictionary associating the
                // JsonWebKey components with their values.
                var parameters = new Dictionary<string, string> {
                    { JsonWebKeyParameterNames.Kid, key.Kid },
                    { JsonWebKeyParameterNames.Use, key.Use },
                    { JsonWebKeyParameterNames.Kty, key.Kty },
                    { JsonWebKeyParameterNames.KeyOps, key.KeyOps },
                    { JsonWebKeyParameterNames.Alg, key.Alg },
                    { JsonWebKeyParameterNames.X5t, key.X5t },
                    { JsonWebKeyParameterNames.X5u, key.X5u },
                    { JsonWebKeyParameterNames.E, key.E },
                    { JsonWebKeyParameterNames.N, key.N }
                };

                foreach (var parameter in parameters) {
                    if (!string.IsNullOrEmpty(parameter.Value)) {
                        item.Add(parameter.Key, parameter.Value);
                    }
                }

                if (key.X5c.Any()) {
                    item.Add(JsonWebKeyParameterNames.X5c, JArray.FromObject(key.X5c));
                }

                keys.Add(item);
            }

            payload.Add(JsonWebKeyParameterNames.Keys, keys);

            var context = new CryptographyEndpointResponseContext(Context, Options, payload);
            await Options.Provider.CryptographyEndpointResponse(context);

            if (context.HandledResponse) {
                return;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer))) {
                payload.WriteTo(writer);
                writer.Flush();

                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Request.CallCancelled);
            }
        }

        private async Task InvokeTokenEndpointAsync() {
            if (!string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed token request has been received: make sure to use POST."
                });

                return;
            }

            // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
            if (string.IsNullOrEmpty(Request.ContentType)) {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed token request has been received: " +
                        "the mandatory 'Content-Type' header was missing from the POST request."
                });

                return;
            }

            // May have media/type; charset=utf-8, allow partial match.
            if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed token request has been received: " +
                        "the 'Content-Type' header contained an unexcepted value. " +
                        "Make sure to use 'application/x-www-form-urlencoded'."
                });

                return;
            }

            var request = new OpenIdConnectMessage(await Request.ReadFormAsync()) {
                RequestType = OpenIdConnectRequestType.TokenRequest
            };

            // Reject token requests missing the mandatory grant_type parameter.
            if (string.IsNullOrEmpty(request.GrantType)) {
                Options.Logger.WriteError("The token request was rejected because the grant type was missing.");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "The mandatory 'grant_type' parameter was missing.",
                });

                return;
            }

            // Reject grant_type=authorization_code requests missing the authorization code.
            // See https://tools.ietf.org/html/rfc6749#section-4.1.3
            else if (request.IsAuthorizationCodeGrantType() && string.IsNullOrEmpty(request.Code)) {
                Options.Logger.WriteError("The token request was rejected because the authorization code was missing.");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "The mandatory 'code' parameter was missing."
                });

                return;
            }

            // Reject grant_type=refresh_token requests missing the refresh token.
            // See https://tools.ietf.org/html/rfc6749#section-6
            else if (request.IsRefreshTokenGrantType() && string.IsNullOrEmpty(request.GetRefreshToken())) {
                Options.Logger.WriteError("The token request was rejected because the refresh token was missing.");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "The mandatory 'refresh_token' parameter was missing."
                });

                return;
            }

            // Reject grant_type=password requests missing username or password.
            // See https://tools.ietf.org/html/rfc6749#section-4.3.2
            else if (request.IsPasswordGrantType() && (string.IsNullOrEmpty(request.Username) ||
                                                       string.IsNullOrEmpty(request.Password))) {
                Options.Logger.WriteError("The token request was rejected because the resource owner credentials were missing.");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "The mandatory 'username' and/or 'password' parameters " +
                                       "was/were missing from the request message."
                });

                return;
            }

            // When client_id and client_secret are both null, try to extract them from the Authorization header.
            // See http://tools.ietf.org/html/rfc6749#section-2.3.1 and
            // http://openid.net/specs/openid-connect-core-1_0.html#ClientAuthentication
            if (string.IsNullOrEmpty(request.ClientId) && string.IsNullOrEmpty(request.ClientSecret)) {
                var header = Request.Headers.Get("Authorization");
                if (!string.IsNullOrEmpty(header) && header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) {
                    try {
                        var value = header.Substring("Basic ".Length).Trim();
                        var data = Encoding.UTF8.GetString(Convert.FromBase64String(value));

                        var index = data.IndexOf(':');
                        if (index >= 0) {
                            request.ClientId = data.Substring(0, index);
                            request.ClientSecret = data.Substring(index + 1);
                        }
                    }

                    catch (FormatException) { }
                    catch (ArgumentException) { }
                }
            }

            var clientNotification = new ValidateClientAuthenticationContext(Context, Options, request);
            await Options.Provider.ValidateClientAuthentication(clientNotification);

            // Reject the request if client authentication was rejected.
            if (clientNotification.IsRejected) {
                Options.Logger.WriteError("invalid client authentication.");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = clientNotification.Error ?? OpenIdConnectConstants.Errors.InvalidClient,
                    ErrorDescription = clientNotification.ErrorDescription,
                    ErrorUri = clientNotification.ErrorUri
                });

                return;
            }

            // Reject grant_type=client_credentials requests if client authentication was skipped.
            else if (clientNotification.IsSkipped && request.IsClientCredentialsGrantType()) {
                Options.Logger.WriteError("client authentication is required for client_credentials grant type.");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidGrant,
                    ErrorDescription = "client authentication is required when using client_credentials"
                });

                return;
            }

            // Ensure that the client_id has been set from the ValidateClientAuthentication event.
            else if (clientNotification.IsValidated && string.IsNullOrEmpty(request.ClientId)) {
                Options.Logger.WriteError("Client authentication was validated but the client_id was not set.");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.ServerError,
                    ErrorDescription = "An internal server error occurred."
                });

                return;
            }

            var validatingContext = new ValidateTokenRequestContext(Context, Options, request);

            // Validate the token request immediately if the grant type used by
            // the client application doesn't rely on a previously-issued token/code.
            if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType()) {
                await Options.Provider.ValidateTokenRequest(validatingContext);

                if (!validatingContext.IsValidated) {
                    // Note: use invalid_request as the default error if none has been explicitly provided.
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = validatingContext.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = validatingContext.ErrorDescription,
                        ErrorUri = validatingContext.ErrorUri
                    });

                    return;
                }
            }

            AuthenticationTicket ticket = null;

            // See http://tools.ietf.org/html/rfc6749#section-4.1
            // and http://tools.ietf.org/html/rfc6749#section-4.1.3 (authorization code grant).
            // See http://tools.ietf.org/html/rfc6749#section-6 (refresh token grant).
            if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType()) {
                ticket = request.IsAuthorizationCodeGrantType() ?
                    await DeserializeAuthorizationCodeAsync(request.Code, request) :
                    await DeserializeRefreshTokenAsync(request.GetRefreshToken(), request);

                if (ticket == null) {
                    Options.Logger.WriteError("invalid ticket");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidGrant,
                        ErrorDescription = "Invalid ticket"
                    });

                    return;
                }

                if (!ticket.Properties.ExpiresUtc.HasValue ||
                     ticket.Properties.ExpiresUtc < Options.SystemClock.UtcNow) {
                    Options.Logger.WriteError("expired ticket");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidGrant,
                        ErrorDescription = "Expired ticket"
                    });

                    return;
                }

                // If the client was fully authenticated when retrieving its refresh token,
                // the current request must be rejected if client authentication was not enforced.
                if (request.IsRefreshTokenGrantType() && !clientNotification.IsValidated && ticket.IsConfidential()) {
                    Options.Logger.WriteError("client authentication is required to use this ticket");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidGrant,
                        ErrorDescription = "Client authentication is required to use this ticket"
                    });

                    return;
                }

                // Note: identifier may be null during a grant_type=refresh_token request if the refresh token
                // was issued to a public client but cannot be null for an authorization code grant request.
                var identifier = ticket.Properties.GetProperty(OpenIdConnectConstants.Extra.ClientId);
                if (request.IsAuthorizationCodeGrantType() && string.IsNullOrEmpty(identifier)) {
                    Options.Logger.WriteError("The client the authorization code was issued to cannot be resolved.");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.ServerError,
                        ErrorDescription = "An internal server error occurred."
                    });

                    return;
                }

                // At this stage, client_id cannot be null for grant_type=authorization_code requests,
                // as it must either be set in the ValidateClientAuthentication notification
                // by the developer or manually flowed by non-confidential client applications.
                // See https://tools.ietf.org/html/rfc6749#section-4.1.3
                if (request.IsAuthorizationCodeGrantType() && string.IsNullOrEmpty(request.ClientId)) {
                    Options.Logger.WriteError("client_id was missing from the token request");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "client_id was missing from the token request"
                    });

                    return;
                }

                // Ensure the authorization code/refresh token was issued to the client application making the token request.
                // Note: when using the refresh token grant, client_id is optional but must validated if present.
                // As a consequence, this check doesn't depend on the actual status of client authentication.
                // See https://tools.ietf.org/html/rfc6749#section-6
                // and http://openid.net/specs/openid-connect-core-1_0.html#RefreshingAccessToken
                if (!string.IsNullOrEmpty(identifier) && !string.IsNullOrEmpty(request.ClientId) &&
                    !string.Equals(identifier, request.ClientId, StringComparison.Ordinal)) {
                    Options.Logger.WriteError("ticket does not contain matching client_id");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidGrant,
                        ErrorDescription = "Ticket does not contain matching client_id"
                    });

                    return;
                }

                // Validate the redirect_uri flowed by the client application during this token request.
                // Note: for pure OAuth2 requests, redirect_uri is only mandatory if the authorization request
                // contained an explicit redirect_uri. OpenID Connect requests MUST include a redirect_uri
                // but the specifications allow proceeding the token request without returning an error
                // if the authorization request didn't contain an explicit redirect_uri.
                // See https://tools.ietf.org/html/rfc6749#section-4.1.3
                // and http://openid.net/specs/openid-connect-core-1_0.html#TokenRequestValidation
                string address;
                if (request.IsAuthorizationCodeGrantType() &&
                    ticket.Properties.Dictionary.TryGetValue(OpenIdConnectConstants.Extra.RedirectUri, out address)) {
                    ticket.Properties.Dictionary.Remove(OpenIdConnectConstants.Extra.RedirectUri);

                    if (string.IsNullOrEmpty(request.RedirectUri)) {
                        Options.Logger.WriteError("redirect_uri was missing from the grant_type=authorization_code request.");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = OpenIdConnectConstants.Errors.InvalidRequest,
                            ErrorDescription = "redirect_uri was missing from the token request"
                        });

                        return;
                    }

                    else if (!string.Equals(address, request.RedirectUri, StringComparison.Ordinal)) {
                        Options.Logger.WriteError("authorization code does not contain matching redirect_uri");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = "Authorization code does not contain matching redirect_uri"
                        });

                        return;
                    }
                }

                if (!string.IsNullOrEmpty(request.Resource)) {
                    // When an explicit resource parameter has been included in the token request
                    // but was missing from the authorization request, the request MUST be rejected.
                    var resources = ticket.Properties.GetResources();
                    if (!resources.Any()) {
                        Options.Logger.WriteError("token request cannot contain a resource");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = "Token request cannot contain a resource parameter" +
                                               "if the authorization request didn't contain one"
                        });

                        return;
                    }

                    // When an explicit resource parameter has been included in the token request,
                    // the authorization server MUST ensure that it doesn't contain resources
                    // that were not allowed during the authorization request.
                    else if (!resources.ContainsSet(request.GetResources())) {
                        Options.Logger.WriteError("token request does not contain matching resource");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = "Token request doesn't contain a valid resource parameter"
                        });

                        return;
                    }

                    // Remove the "resource" property from the authentication ticket corresponding
                    // to the authorization code/refresh token to force the token endpoint
                    // to use the "resource" parameter flowed in the token request.
                    ticket.Properties.Dictionary.Remove(OpenIdConnectConstants.Extra.Resource);
                }

                else {
                    // When no explicit "resource" parameter has been received, the "resource" parameter sent
                    // during the authorization request or the previous token request is used instead.
                    request.Resource = ticket.GetProperty(OpenIdConnectConstants.Extra.Resource);
                }

                if (!string.IsNullOrEmpty(request.Scope)) {
                    // When an explicit scope parameter has been included in the token request
                    // but was missing from the authorization request, the request MUST be rejected.
                    // See http://tools.ietf.org/html/rfc6749#section-6
                    var scopes = ticket.Properties.GetScopes();
                    if (!scopes.Any()) {
                        Options.Logger.WriteError("token request cannot contain a scope");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = "Token request cannot contain a scope parameter" +
                                               "if the authorization request didn't contain one"
                        });

                        return;
                    }

                    // When an explicit scope parameter has been included in the token request,
                    // the authorization server MUST ensure that it doesn't contain scopes
                    // that were not allowed during the authorization request.
                    // See https://tools.ietf.org/html/rfc6749#section-6
                    else if (!scopes.ContainsSet(request.GetScopes())) {
                        Options.Logger.WriteError("authorization code does not contain matching scope");

                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = "Token request doesn't contain a valid scope parameter"
                        });

                        return;
                    }

                    // Remove the "scope" property from the authentication ticket corresponding
                    // to the authorization code/refresh token to force the token endpoint
                    // to use the "scope" parameter flowed in the token request.
                    ticket.Properties.Dictionary.Remove(OpenIdConnectConstants.Extra.Scope);
                }

                else {
                    // When no explicit "scope" parameter has been received, the "scope" parameter sent
                    // during the authorization request or the previous token request is used instead.
                    request.Scope = ticket.GetProperty(OpenIdConnectConstants.Extra.Scope);
                }

                // Expose the authentication ticket extracted from the authorization
                // code or the refresh token before invoking ValidateTokenRequest.
                validatingContext.AuthenticationTicket = ticket;

                await Options.Provider.ValidateTokenRequest(validatingContext);

                if (!validatingContext.IsValidated) {
                    // Note: use invalid_request as the default error if none has been explicitly provided.
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = validatingContext.Error ?? OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = validatingContext.ErrorDescription,
                        ErrorUri = validatingContext.ErrorUri
                    });

                    return;
                }

                if (request.IsAuthorizationCodeGrantType()) {
                    // Note: the authentication ticket is copied to avoid modifying the properties of the authorization code.
                    var context = new GrantAuthorizationCodeContext(Context, Options, request, ticket.Copy());
                    await Options.Provider.GrantAuthorizationCode(context);

                    if (!context.IsValidated) {
                        // Note: use invalid_grant as the default error if none has been explicitly provided.
                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = context.Error ?? OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = context.ErrorDescription,
                            ErrorUri = context.ErrorUri
                        });

                        return;
                    }

                    ticket = context.AuthenticationTicket;
                }

                else {
                    // Note: the authentication ticket is copied to avoid modifying the properties of the refresh token.
                    var context = new GrantRefreshTokenContext(Context, Options, request, ticket.Copy());
                    await Options.Provider.GrantRefreshToken(context);

                    if (!context.IsValidated) {
                        // Note: use invalid_grant as the default error if none has been explicitly provided.
                        await SendErrorPayloadAsync(new OpenIdConnectMessage {
                            Error = context.Error ?? OpenIdConnectConstants.Errors.InvalidGrant,
                            ErrorDescription = context.ErrorDescription,
                            ErrorUri = context.ErrorUri
                        });

                        return;
                    }

                    ticket = context.AuthenticationTicket;
                }

                // By default, when using the authorization code or the refresh token grants, the authentication ticket
                // extracted from the code/token is used as-is. If the developer didn't provide his own ticket
                // or didn't set an explicit expiration date, the ticket properties are reset to avoid aligning the
                // expiration date of the generated tokens with the lifetime of the authorization code/refresh token.
                if (ticket.Properties.IssuedUtc == validatingContext.AuthenticationTicket.Properties.IssuedUtc) {
                    ticket.Properties.IssuedUtc = null;
                }

                if (ticket.Properties.ExpiresUtc == validatingContext.AuthenticationTicket.Properties.ExpiresUtc) {
                    ticket.Properties.ExpiresUtc = null;
                }
            }

            // See http://tools.ietf.org/html/rfc6749#section-4.3
            // and http://tools.ietf.org/html/rfc6749#section-4.3.2
            else if (request.IsPasswordGrantType()) {
                var context = new GrantResourceOwnerCredentialsContext(Context, Options, request);
                await Options.Provider.GrantResourceOwnerCredentials(context);

                if (!context.IsValidated) {
                    // Note: use invalid_grant as the default error if none has been explicitly provided.
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = context.Error ?? OpenIdConnectConstants.Errors.InvalidGrant,
                        ErrorDescription = context.ErrorDescription,
                        ErrorUri = context.ErrorUri
                    });

                    return;
                }

                ticket = context.AuthenticationTicket;
            }

            // See http://tools.ietf.org/html/rfc6749#section-4.4
            // and http://tools.ietf.org/html/rfc6749#section-4.4.2
            else if (request.IsClientCredentialsGrantType()) {
                var context = new GrantClientCredentialsContext(Context, Options, request);
                await Options.Provider.GrantClientCredentials(context);

                if (!context.IsValidated) {
                    // Note: use unauthorized_client as the default error if none has been explicitly provided.
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = context.Error ?? OpenIdConnectConstants.Errors.UnauthorizedClient,
                        ErrorDescription = context.ErrorDescription,
                        ErrorUri = context.ErrorUri
                    });

                    return;
                }

                ticket = context.AuthenticationTicket;
            }

            // See http://tools.ietf.org/html/rfc6749#section-8.3
            else {
                var context = new GrantCustomExtensionContext(Context, Options, request);
                await Options.Provider.GrantCustomExtension(context);

                if (!context.IsValidated) {
                    // Note: use unsupported_grant_type as the default error if none has been explicitly provided.
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = context.Error ?? OpenIdConnectConstants.Errors.UnsupportedGrantType,
                        ErrorDescription = context.ErrorDescription,
                        ErrorUri = context.ErrorUri
                    });

                    return;
                }

                ticket = context.AuthenticationTicket;
            }

            var notification = new TokenEndpointContext(Context, Options, request, ticket);
            await Options.Provider.TokenEndpoint(notification);

            if (notification.HandledResponse) {
                return;
            }

            // Flow the changes made to the ticket.
            ticket = notification.Ticket;

            // Ensure an authentication ticket has been provided:
            // a null ticket MUST result in an internal server error.
            if (ticket == null) {
                Options.Logger.WriteError("authentication ticket missing");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.ServerError
                });

                return;
            }

            if (!string.IsNullOrEmpty(request.ClientId)) {
                // Keep the original client_id parameter for later comparison.
                ticket.Properties.Dictionary[OpenIdConnectConstants.Extra.ClientId] = request.ClientId;
            }

            // Note: the application is allowed to specify a different "resource": in this case,
            // don't replace the "resource" property stored in the authentication ticket.
            if (!string.IsNullOrEmpty(request.Resource) &&
                !ticket.Properties.Dictionary.ContainsKey(OpenIdConnectConstants.Extra.Resource)) {
                // Keep the original resource parameter for later comparison.
                ticket.Properties.Dictionary[OpenIdConnectConstants.Extra.Resource] = request.Resource;
            }

            // Note: the application is allowed to specify a different "scope": in this case,
            // don't replace the "scope" property stored in the authentication ticket.
            if (!string.IsNullOrEmpty(request.Scope) &&
                !ticket.Properties.Dictionary.ContainsKey(OpenIdConnectConstants.Extra.Scope)) {
                // Keep the original scope parameter for later comparison.
                ticket.Properties.Dictionary[OpenIdConnectConstants.Extra.Scope] = request.Scope;
            }

            if (clientNotification.IsValidated) {
                // Store a boolean indicating whether the ticket should be marked as confidential.
                ticket.Properties.Dictionary[OpenIdConnectConstants.Extra.Confidential] = "true";
            }

            var response = new OpenIdConnectMessage();

            // Note: by default, an access token is always returned, but the client application can use the "response_type" parameter
            // to only include specific types of tokens. When this parameter is missing, an access token is always generated.
            if (string.IsNullOrEmpty(request.ResponseType) || request.ContainsResponseType(OpenIdConnectConstants.ResponseTypes.Token)) {
                // Make sure to create a copy of the authentication properties
                // to avoid modifying the properties set on the original ticket.
                var properties = ticket.Properties.Copy();

                // Note: when the "resource" parameter added to the OpenID Connect response
                // is identical to the request parameter, keeping it is not necessary.
                var resource = properties.GetProperty(OpenIdConnectConstants.Extra.Resource);
                if (request.IsAuthorizationCodeGrantType() || !string.Equals(request.Resource, resource, StringComparison.Ordinal)) {
                    response.Resource = resource;
                }

                // Note: when the "scope" parameter added to the OpenID Connect response
                // is identical to the request parameter, keeping it is not necessary.
                var scope = properties.GetProperty(OpenIdConnectConstants.Extra.Scope);
                if (request.IsAuthorizationCodeGrantType() || !string.Equals(request.Scope, scope, StringComparison.Ordinal)) {
                    response.Scope = scope;
                }

                // When sliding expiration is disabled, the access token added to the response
                // cannot live longer than the refresh token that was used in the token request.
                if (request.IsRefreshTokenGrantType() && !Options.UseSlidingExpiration &&
                    validatingContext.AuthenticationTicket.Properties.ExpiresUtc.HasValue &&
                    validatingContext.AuthenticationTicket.Properties.ExpiresUtc.Value <
                        (Options.SystemClock.UtcNow + Options.AccessTokenLifetime)) {
                    properties.ExpiresUtc = validatingContext.AuthenticationTicket.Properties.ExpiresUtc;
                }

                response.TokenType = OpenIdConnectConstants.TokenTypes.Bearer;
                response.AccessToken = await SerializeAccessTokenAsync(ticket.Identity, properties, request, response);

                // Ensure that an access token is issued to avoid returning an invalid response.
                // See http://openid.net/specs/oauth-v2-multiple-response-types-1_0.html#Combinations
                if (string.IsNullOrEmpty(response.AccessToken)) {
                    Options.Logger.WriteError("SerializeAccessTokenAsync returned no access token.");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.ServerError,
                        ErrorDescription = "no valid access token was issued"
                    });

                    return;
                }

                // properties.ExpiresUtc is automatically set by SerializeAccessTokenAsync but the end user
                // is free to set a null value directly in the SerializeAccessToken event.
                if (properties.ExpiresUtc.HasValue && properties.ExpiresUtc > Options.SystemClock.UtcNow) {
                    var lifetime = properties.ExpiresUtc.Value - Options.SystemClock.UtcNow;
                    var expiration = (long) (lifetime.TotalSeconds + .5);

                    response.ExpiresIn = expiration.ToString(CultureInfo.InvariantCulture);
                }
            }

            // Note: by default, an identity token is always returned when the "openid" scope has been requested,
            // but the client application can use the "response_type" parameter to only include specific types of tokens.
            // When this parameter is missing, an identity token is always generated.
            if (request.ContainsScope(OpenIdConnectConstants.Scopes.OpenId) && (string.IsNullOrEmpty(request.ResponseType) ||
                                                                                request.ContainsResponseType("id_token"))) {
                // Make sure to create a copy of the authentication properties
                // to avoid modifying the properties set on the original ticket.
                var properties = ticket.Properties.Copy();

                // When sliding expiration is disabled, the identity token added to the response
                // cannot live longer than the refresh token that was used in the token request.
                if (request.IsRefreshTokenGrantType() && !Options.UseSlidingExpiration &&
                    validatingContext.AuthenticationTicket.Properties.ExpiresUtc.HasValue &&
                    validatingContext.AuthenticationTicket.Properties.ExpiresUtc.Value <
                        (Options.SystemClock.UtcNow + Options.IdentityTokenLifetime)) {
                    properties.ExpiresUtc = validatingContext.AuthenticationTicket.Properties.ExpiresUtc;
                }

                response.IdToken = await SerializeIdentityTokenAsync(ticket.Identity, properties, request, response);

                // Ensure that an identity token is issued to avoid returning an invalid response.
                // See http://openid.net/specs/openid-connect-core-1_0.html#TokenResponse
                // and http://openid.net/specs/openid-connect-core-1_0.html#RefreshTokenResponse
                if (string.IsNullOrEmpty(response.IdToken)) {
                    Options.Logger.WriteError("SerializeIdentityTokenAsync returned no identity token.");

                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.ServerError,
                        ErrorDescription = "no valid identity token was issued"
                    });

                    return;
                }
            }

            // Note: by default, a refresh token is always returned when the "offline_access" scope has been requested,
            // but the client application can use the "response_type" parameter to only include specific types of tokens.
            // When this parameter is missing, a refresh token is always generated.
            if (request.ContainsScope(OpenIdConnectConstants.Scopes.OfflineAccess) && (string.IsNullOrEmpty(request.ResponseType) ||
                                                                                       request.ContainsResponseType("refresh_token"))) {
                // Make sure to create a copy of the authentication properties
                // to avoid modifying the properties set on the original ticket.
                var properties = ticket.Properties.Copy();

                // When sliding expiration is disabled, the refresh token added to the response
                // cannot live longer than the refresh token that was used in the token request.
                if (request.IsRefreshTokenGrantType() && !Options.UseSlidingExpiration &&
                    validatingContext.AuthenticationTicket.Properties.ExpiresUtc.HasValue &&
                    validatingContext.AuthenticationTicket.Properties.ExpiresUtc.Value <
                        (Options.SystemClock.UtcNow + Options.RefreshTokenLifetime)) {
                    properties.ExpiresUtc = validatingContext.AuthenticationTicket.Properties.ExpiresUtc;
                }

                response.SetRefreshToken(await SerializeRefreshTokenAsync(ticket.Identity, properties, request, response));
            }

            var payload = new JObject();

            foreach (var parameter in response.Parameters) {
                payload.Add(parameter.Key, parameter.Value);
            }

            var responseNotification = new TokenEndpointResponseContext(Context, Options, ticket, request, payload);
            await Options.Provider.TokenEndpointResponse(responseNotification);

            if (responseNotification.HandledResponse) {
                return;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer))) {
                payload.WriteTo(writer);
                writer.Flush();

                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                Response.Headers.Set("Cache-Control", "no-cache");
                Response.Headers.Set("Pragma", "no-cache");
                Response.Headers.Set("Expires", "-1");

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Request.CallCancelled);
            }
        }

        private async Task<bool> InvokeProfileEndpointAsync() {
            OpenIdConnectMessage request;

            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed userinfo request has been received: " +
                        "make sure to use either GET or POST."
                });

                return true;
            }

            if (string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                request = new OpenIdConnectMessage(Request.Query);
            }

            else {
                // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
                if (string.IsNullOrWhiteSpace(Request.ContentType)) {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed userinfo request has been received: " +
                            "the mandatory 'Content-Type' header was missing from the POST request."
                    });

                    return true;
                }

                // May have media/type; charset=utf-8, allow partial match.
                if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed userinfo request has been received: " +
                            "the 'Content-Type' header contained an unexcepted value. " +
                            "Make sure to use 'application/x-www-form-urlencoded'."
                    });

                    return true;
                }

                request = new OpenIdConnectMessage(await Request.ReadFormAsync());
            }

            string token;
            if (!string.IsNullOrEmpty(request.AccessToken)) {
                token = request.AccessToken;
            }

            else {
                var header = Request.Headers.Get("Authorization");
                if (string.IsNullOrEmpty(header)) {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed userinfo request has been received."
                    });

                    return true;
                }

                if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed userinfo request has been received."
                    });

                    return true;
                }

                token = header.Substring("Bearer ".Length);
                if (string.IsNullOrEmpty(token)) {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed userinfo request has been received."
                    });

                    return true;
                }
            }

            var ticket = await DeserializeAccessTokenAsync(token, request);
            if (ticket == null) {
                Options.Logger.WriteError("invalid token");

                // Note: an invalid token should result in an unauthorized response
                // but returning a 401 status would invoke the previously registered
                // authentication middleware and potentially replace it by a 302 response.
                // To work around this limitation, a 400 error is returned instead.
                // See http://openid.net/specs/openid-connect-core-1_0.html#UserInfoError
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidGrant,
                    ErrorDescription = "Invalid token."
                });

                return true;
            }

            if (!ticket.Properties.ExpiresUtc.HasValue ||
                 ticket.Properties.ExpiresUtc < Options.SystemClock.UtcNow) {
                Options.Logger.WriteError("expired token");

                // Note: an invalid token should result in an unauthorized response
                // but returning a 401 status would invoke the previously registered
                // authentication middleware and potentially replace it by a 302 response.
                // To work around this limitation, a 400 error is returned instead.
                // See http://openid.net/specs/openid-connect-core-1_0.html#UserInfoError
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidGrant,
                    ErrorDescription = "Expired token."
                });

                return true;
            }

            // Insert the userinfo request in the ASP.NET context.
            Context.SetOpenIdConnectRequest(request);

            var notification = new ProfileEndpointContext(Context, Options, request, ticket);

            // 'sub' is a mandatory claim but is not necessarily present as-is: when missing,
            // the name identifier extracted from the authentication ticket is used instead.
            // See http://openid.net/specs/openid-connect-core-1_0.html#UserInfoResponse
            notification.Subject = ticket.Identity.GetClaim(JwtRegisteredClaimNames.Sub) ??
                                   ticket.Identity.GetClaim(ClaimTypes.NameIdentifier);

            notification.Audience = ticket.Identity.GetClaim(JwtRegisteredClaimNames.Azp);

            notification.Issuer = Context.GetIssuer(Options);

            // The following claims are all optional and should be excluded when
            // no corresponding value has been found in the authentication ticket.
            if (ticket.ContainsScope(OpenIdConnectConstants.Scopes.Profile)) {
                notification.FamilyName = ticket.Identity.GetClaim(ClaimTypes.Surname);
                notification.GivenName = ticket.Identity.GetClaim(ClaimTypes.GivenName);
                notification.BirthDate = ticket.Identity.GetClaim(ClaimTypes.DateOfBirth);
            }

            if (ticket.ContainsScope(OpenIdConnectConstants.Scopes.Email)) {
                notification.Email = ticket.Identity.GetClaim(ClaimTypes.Email);
            };

            if (ticket.ContainsScope(OpenIdConnectConstants.Scopes.Phone)) {
                notification.PhoneNumber = ticket.Identity.GetClaim(ClaimTypes.HomePhone) ??
                                           ticket.Identity.GetClaim(ClaimTypes.MobilePhone) ??
                                           ticket.Identity.GetClaim(ClaimTypes.OtherPhone);
            };

            await Options.Provider.ProfileEndpoint(notification);

            if (notification.HandledResponse) {
                return true;
            }

            else if (notification.Skipped) {
                return false;
            }

            // Ensure the "sub" claim has been correctly populated.
            if (string.IsNullOrEmpty(notification.Subject)) {
                Options.Logger.WriteError("The mandatory 'sub' claim was missing from the userinfo response.");

                Response.StatusCode = 500;

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.ServerError,
                    ErrorDescription = "The mandatory 'sub' claim was missing."
                });

                return true;
            }

            var payload = new JObject {
                [JwtRegisteredClaimNames.Sub] = notification.Subject
            };

            if (notification.Address != null) {
                payload[OpenIdConnectConstants.Claims.Address] = notification.Address;
            }

            if (!string.IsNullOrEmpty(notification.Audience)) {
                payload[JwtRegisteredClaimNames.Aud] = notification.Audience;
            }

            if (!string.IsNullOrEmpty(notification.BirthDate)) {
                payload[JwtRegisteredClaimNames.Birthdate] = notification.BirthDate;
            }

            if (!string.IsNullOrEmpty(notification.Email)) {
                payload[JwtRegisteredClaimNames.Email] = notification.Email;
            }

            if (notification.EmailVerified.HasValue) {
                payload[OpenIdConnectConstants.Claims.EmailVerified] = notification.EmailVerified.Value;
            }

            if (!string.IsNullOrEmpty(notification.FamilyName)) {
                payload[JwtRegisteredClaimNames.FamilyName] = notification.FamilyName;
            }

            if (!string.IsNullOrEmpty(notification.GivenName)) {
                payload[JwtRegisteredClaimNames.GivenName] = notification.GivenName;
            }

            if (!string.IsNullOrEmpty(notification.Issuer)) {
                payload[JwtRegisteredClaimNames.Iss] = notification.Issuer;
            }

            if (!string.IsNullOrEmpty(notification.PhoneNumber)) {
                payload[OpenIdConnectConstants.Claims.PhoneNumber] = notification.PhoneNumber;
            }

            if (notification.PhoneNumberVerified.HasValue) {
                payload[OpenIdConnectConstants.Claims.PhoneNumberVerified] = notification.PhoneNumberVerified.Value;
            }

            if (!string.IsNullOrEmpty(notification.PreferredUsername)) {
                payload[OpenIdConnectConstants.Claims.PreferredUsername] = notification.PreferredUsername;
            }

            if (!string.IsNullOrEmpty(notification.Profile)) {
                payload[OpenIdConnectConstants.Claims.Profile] = notification.Profile;
            }

            if (!string.IsNullOrEmpty(notification.Website)) {
                payload[OpenIdConnectConstants.Claims.Website] = notification.Website;
            }

            foreach (var claim in notification.Claims) {
                // Ignore claims whose value is null.
                if (claim.Value == null) {
                    continue;
                }

                payload.Add(claim.Key, claim.Value);
            }

            var context = new ProfileEndpointResponseContext(Context, Options, request, payload);
            await Options.Provider.ProfileEndpointResponse(context);

            if (context.HandledResponse) {
                return true;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer))) {
                payload.WriteTo(writer);
                writer.Flush();

                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                Response.Headers.Set("Cache-Control", "no-cache");
                Response.Headers.Set("Pragma", "no-cache");
                Response.Headers.Set("Expires", "-1");

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Request.CallCancelled);
            }

            return true;
        }

        private async Task InvokeValidationEndpointAsync() {
            OpenIdConnectMessage request;

            // See https://tools.ietf.org/html/rfc7662#section-2.1
            // and https://tools.ietf.org/html/rfc7662#section-4
            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed validation request has been received: " +
                        "make sure to use either GET or POST."
                });

                return;
            }

            if (string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                request = new OpenIdConnectMessage(Request.Query) {
                    RequestType = OpenIdConnectRequestType.AuthenticationRequest
                };
            }

            else {
                // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
                if (string.IsNullOrEmpty(Request.ContentType)) {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed validation request has been received: " +
                            "the mandatory 'Content-Type' header was missing from the POST request."
                    });

                    return;
                }

                // May have media/type; charset=utf-8, allow partial match.
                if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) {
                    await SendErrorPayloadAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed validation request has been received: " +
                            "the 'Content-Type' header contained an unexcepted value. " +
                            "Make sure to use 'application/x-www-form-urlencoded'."
                    });

                    return;
                }

                request = new OpenIdConnectMessage(await Request.ReadFormAsync()) {
                    RequestType = OpenIdConnectRequestType.AuthenticationRequest
                };
            }

            if (string.IsNullOrWhiteSpace(request.Token)) {
                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed validation request has been received: " +
                        "a 'token' parameter with an access, refresh, or identity token is required."
                });

                return;
            }

            var clientNotification = new ValidateClientAuthenticationContext(Context, Options, request);
            await Options.Provider.ValidateClientAuthentication(clientNotification);

            // Reject the request if client authentication was rejected.
            if (clientNotification.IsRejected) {
                Options.Logger.WriteError("The validation request was rejected " +
                                          "because client authentication was invalid.");

                await SendPayloadAsync(new JObject {
                    [OpenIdConnectConstants.Claims.Active] = false
                });

                return;
            }

            // Ensure that the client_id has been set from the ValidateClientAuthentication event.
            else if (clientNotification.IsValidated && string.IsNullOrEmpty(request.ClientId)) {
                Options.Logger.WriteError("Client authentication was validated but the client_id was not set.");

                await SendErrorPayloadAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.ServerError,
                    ErrorDescription = "An internal server error occurred."
                });

                return;
            }

            AuthenticationTicket ticket = null;

            // Note: use the "token_type_hint" parameter to determine
            // the type of the token sent by the client application.
            // See https://tools.ietf.org/html/rfc7662#section-2.1
            switch (request.GetTokenTypeHint()) {
                case OpenIdConnectConstants.Usages.AccessToken:
                    ticket = await DeserializeAccessTokenAsync(request.Token, request);
                    break;

                case OpenIdConnectConstants.Usages.RefreshToken:
                    ticket = await DeserializeRefreshTokenAsync(request.Token, request);
                    break;

                case OpenIdConnectConstants.Usages.IdToken:
                    ticket = await DeserializeIdentityTokenAsync(request.Token, request);
                    break;
            }

            // Note: if the token can't be found using "token_type_hint",
            // extend the search across all of the supported token types.
            // See https://tools.ietf.org/html/rfc7662#section-2.1
            if (ticket == null) {
                ticket = await DeserializeAccessTokenAsync(request.Token, request) ??
                         await DeserializeIdentityTokenAsync(request.Token, request) ??
                         await DeserializeRefreshTokenAsync(request.Token, request);
            }

            if (ticket == null) {
                Options.Logger.WriteInformation("The validation request was rejected because the token was invalid.");

                await SendPayloadAsync(new JObject {
                    [OpenIdConnectConstants.Claims.Active] = false
                });

                return;
            }

            // Note: unlike refresh or identity tokens that can only be validated by client applications,
            // access tokens can be validated by either resource servers or client applications:
            // in both cases, the caller must be authenticated if the ticket is marked as confidential.
            if (clientNotification.IsSkipped && ticket.IsConfidential()) {
                Options.Logger.WriteWarning("The validation request was rejected " +
                                            "because the caller was not authenticated.");

                await SendPayloadAsync(new JObject {
                    [OpenIdConnectConstants.Claims.Active] = false
                });

                return;
            }

            // If the ticket is already expired, directly return active=false.
            if (ticket.Properties.ExpiresUtc.HasValue &&
                ticket.Properties.ExpiresUtc < Options.SystemClock.UtcNow) {
                Options.Logger.WriteVerbose("expired token");

                await SendPayloadAsync(new JObject {
                    [OpenIdConnectConstants.Claims.Active] = false
                });

                return;
            }

            if (ticket.IsAccessToken()) {
                // When the caller is authenticated, ensure it is
                // listed as a valid audience or authorized party.
                var audiences = ticket.GetAudiences();
                if (clientNotification.IsValidated && !audiences.Contains(clientNotification.ClientId, StringComparer.Ordinal) &&
                                                      !ticket.Identity.HasClaim(JwtRegisteredClaimNames.Azp, clientNotification.ClientId)) {
                    Options.Logger.WriteWarning("The validation request was rejected because the access token " +
                                                "was issued to a different client or for another resource server.");

                    await SendPayloadAsync(new JObject {
                        [OpenIdConnectConstants.Claims.Active] = false
                    });

                    return;
                }
            }

            else if (ticket.IsIdentityToken()) {
                // When the caller is authenticated, reject the validation
                // request if the caller is not listed as a valid audience.
                var audiences = ticket.GetAudiences();
                if (clientNotification.IsValidated && !audiences.Contains(clientNotification.ClientId, StringComparer.Ordinal)) {
                    Options.Logger.WriteWarning("The validation request was rejected because the " +
                                                "identity token was issued to a different client.");

                    await SendPayloadAsync(new JObject {
                        [OpenIdConnectConstants.Claims.Active] = false
                    });

                    return;
                }
            }

            else if (ticket.IsRefreshToken()) {
                // When the caller is authenticated, reject the validation request if the caller
                // doesn't correspond to the client application the token was issued to.
                var identifier = ticket.GetProperty(OpenIdConnectConstants.Extra.ClientId);
                if (clientNotification.IsValidated && !string.Equals(identifier, clientNotification.ClientId, StringComparison.Ordinal)) {
                    Options.Logger.WriteWarning("The validation request was rejected because the " +
                                                "refresh token was issued to a different client.");

                    await SendPayloadAsync(new JObject {
                        [OpenIdConnectConstants.Claims.Active] = false
                    });

                    return;
                }
            }

            // Insert the validation request in the ASP.NET context.
            Context.SetOpenIdConnectRequest(request);

            var notification = new ValidationEndpointContext(Context, Options, request, ticket);
            notification.Active = true;

            // Note: "token_type" may be null when the received token is not an access token.
            // See https://tools.ietf.org/html/rfc7662#section-2.2 and https://tools.ietf.org/html/rfc6749#section-5.1
            notification.TokenType = ticket.Identity.GetClaim(OpenIdConnectConstants.Claims.TokenType);

            // Try to resolve the issuer from the "iss" claim extracted from the token.
            // If none can be found, a generic value is determined from the value
            // registered in the options or from the current URL.
            notification.Issuer = ticket.Identity.GetClaim(JwtRegisteredClaimNames.Iss) ??
                                  Context.GetIssuer(Options);

            notification.Subject = ticket.Identity.GetClaim(JwtRegisteredClaimNames.Sub) ??
                                   ticket.Identity.GetClaim(ClaimTypes.NameIdentifier);

            notification.IssuedAt = ticket.Properties.IssuedUtc;
            notification.ExpiresAt = ticket.Properties.ExpiresUtc;

            // Copy the audiences extracted from the "aud" claim.
            foreach (var audience in ticket.GetAudiences()) {
                notification.Audiences.Add(audience);
            }

            // Note: non-metadata claims are only added if the caller is authenticated AND is in the specified audiences.
            if (clientNotification.IsValidated && notification.Audiences.Contains(clientNotification.ClientId)) {
                notification.Username = ticket.Identity.Name;
                notification.Scope = ticket.GetProperty(OpenIdConnectConstants.Extra.Scope);

                // Potentially sensitive claims are only exposed to trusted callers
                // if the ticket corresponds to an access or identity token.
                if (ticket.IsAccessToken() || ticket.IsIdentityToken()) {
                    foreach (var claim in ticket.Identity.Claims) {
                        // Exclude standard claims, that are already handled via strongly-typed properties.
                        // Make sure to always update this list when adding new built-in claim properties.
                        if (string.Equals(claim.Type, ticket.Identity.NameClaimType, StringComparison.Ordinal) ||
                            string.Equals(claim.Type, ClaimTypes.NameIdentifier, StringComparison.Ordinal)) {
                            continue;
                        }

                        if (string.Equals(claim.Type, JwtRegisteredClaimNames.Aud, StringComparison.Ordinal) ||
                            string.Equals(claim.Type, JwtRegisteredClaimNames.Exp, StringComparison.Ordinal) ||
                            string.Equals(claim.Type, JwtRegisteredClaimNames.Iat, StringComparison.Ordinal) ||
                            string.Equals(claim.Type, JwtRegisteredClaimNames.Iss, StringComparison.Ordinal) ||
                            string.Equals(claim.Type, JwtRegisteredClaimNames.Nbf, StringComparison.Ordinal) ||
                            string.Equals(claim.Type, JwtRegisteredClaimNames.Sub, StringComparison.Ordinal)) {
                            continue;
                        }

                        if (string.Equals(claim.Type, OpenIdConnectConstants.Claims.TokenType, StringComparison.Ordinal) ||
                            string.Equals(claim.Type, OpenIdConnectConstants.Claims.Scope, StringComparison.Ordinal)) {
                            continue;
                        }

                        string type;
                        // Try to resolve the short name associated with the claim type:
                        // if none can be found, the claim type is used as-is.
                        if (!JwtSecurityTokenHandler.OutboundClaimTypeMap.TryGetValue(claim.Type, out type)) {
                            type = claim.Type;
                        }

                        // Note: make sure to use the indexer
                        // syntax to avoid duplicate properties.
                        notification.Claims[type] = claim.Value;
                    }
                }
            }

            await Options.Provider.ValidationEndpoint(notification);

            // Flow the changes made to the authentication ticket.
            ticket = notification.AuthenticationTicket;

            if (notification.HandledResponse) {
                return;
            }

            var payload = new JObject();

            payload.Add(OpenIdConnectConstants.Claims.Active, notification.Active);

            if (!string.IsNullOrEmpty(notification.Issuer)) {
                payload.Add(JwtRegisteredClaimNames.Iss, notification.Issuer);
            }

            if (!string.IsNullOrEmpty(notification.Username)) {
                payload.Add(OpenIdConnectConstants.Claims.Username, notification.Username);
            }

            if (!string.IsNullOrEmpty(notification.Subject)) {
                payload.Add(JwtRegisteredClaimNames.Sub, notification.Subject);
            }

            if (!string.IsNullOrEmpty(notification.Scope)) {
                payload.Add(OpenIdConnectConstants.Claims.Scope, notification.Scope);
            }

            if (notification.IssuedAt.HasValue) {
                payload.Add(JwtRegisteredClaimNames.Iat, EpochTime.GetIntDate(notification.IssuedAt.Value.UtcDateTime));
                payload.Add(JwtRegisteredClaimNames.Nbf, EpochTime.GetIntDate(notification.IssuedAt.Value.UtcDateTime));
            }

            if (notification.ExpiresAt.HasValue) {
                payload.Add(JwtRegisteredClaimNames.Exp, EpochTime.GetIntDate(notification.ExpiresAt.Value.UtcDateTime));
            }

            if (!string.IsNullOrEmpty(notification.TokenType)) {
                payload.Add(OpenIdConnectConstants.Claims.TokenType, notification.TokenType);
            }

            switch (notification.Audiences.Count) {
                case 0: break;

                case 1:
                    payload.Add(JwtRegisteredClaimNames.Aud, notification.Audiences[0]);
                    break;

                default:
                    payload.Add(JwtRegisteredClaimNames.Aud, JArray.FromObject(notification.Audiences));
                    break;
            }

            foreach (var claim in notification.Claims) {
                // Ignore claims whose value is null.
                if (claim.Value == null) {
                    continue;
                }

                // Note: make sure to use the indexer
                // syntax to avoid duplicate properties.
                payload[claim.Key] = claim.Value;
            }

            var context = new ValidationEndpointResponseContext(Context, Options, payload);
            await Options.Provider.ValidationEndpointResponse(context);

            if (context.HandledResponse) {
                return;
            }

            using (var buffer = new MemoryStream())
            using (var writer = new JsonTextWriter(new StreamWriter(buffer))) {
                payload.WriteTo(writer);
                writer.Flush();
                
                Response.ContentLength = buffer.Length;
                Response.ContentType = "application/json;charset=UTF-8";

                Response.Headers.Set("Cache-Control", "no-cache");
                Response.Headers.Set("Pragma", "no-cache");
                Response.Headers.Set("Expires", "-1");

                buffer.Seek(offset: 0, loc: SeekOrigin.Begin);
                await buffer.CopyToAsync(Response.Body, 4096, Request.CallCancelled);
            }
        }

        private async Task<bool> InvokeLogoutEndpointAsync() {
            OpenIdConnectMessage request = null;

            // In principle, logout requests must be made via GET. Nevertheless,
            // POST requests are also allowed so that the inner application can display a logout form.
            // See https://openid.net/specs/openid-connect-session-1_0.html#RPLogout
            if (!string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)) {
                return await SendErrorPageAsync(new OpenIdConnectMessage {
                    Error = OpenIdConnectConstants.Errors.InvalidRequest,
                    ErrorDescription = "A malformed logout request has been received: " +
                        "make sure to use either GET or POST."
                });
            }

            if (string.Equals(Request.Method, "GET", StringComparison.OrdinalIgnoreCase)) {
                request = new OpenIdConnectMessage(Request.Query) {
                    RequestType = OpenIdConnectRequestType.LogoutRequest
                };
            }

            else {
                // See http://openid.net/specs/openid-connect-core-1_0.html#FormSerialization
                if (string.IsNullOrEmpty(Request.ContentType)) {
                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed logout request has been received: " +
                            "the mandatory 'Content-Type' header was missing from the POST request."
                    });
                }

                // May have media/type; charset=utf-8, allow partial match.
                if (!Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)) {
                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = OpenIdConnectConstants.Errors.InvalidRequest,
                        ErrorDescription = "A malformed logout request has been received: " +
                            "the 'Content-Type' header contained an unexcepted value. " +
                            "Make sure to use 'application/x-www-form-urlencoded'."
                    });
                }

                request = new OpenIdConnectMessage(await Request.ReadFormAsync()) {
                    RequestType = OpenIdConnectRequestType.LogoutRequest
                };
            }

            // Store the logout request in the OWIN context.
            Context.SetOpenIdConnectRequest(request);

            // Note: post_logout_redirect_uri is not a mandatory parameter.
            // See http://openid.net/specs/openid-connect-session-1_0.html#RPLogout
            if (!string.IsNullOrEmpty(request.PostLogoutRedirectUri)) {
                var clientNotification = new ValidateClientLogoutRedirectUriContext(Context, Options, request);
                await Options.Provider.ValidateClientLogoutRedirectUri(clientNotification);

                if (clientNotification.IsRejected) {
                    Options.Logger.WriteVerbose("Unable to validate client information");

                    return await SendErrorPageAsync(new OpenIdConnectMessage {
                        Error = clientNotification.Error,
                        ErrorDescription = clientNotification.ErrorDescription,
                        ErrorUri = clientNotification.ErrorUri
                    });
                }
            }

            var notification = new LogoutEndpointContext(Context, Options, request);
            await Options.Provider.LogoutEndpoint(notification);

            if (notification.HandledResponse) {
                return true;
            }

            return false;
        }
    }
}
