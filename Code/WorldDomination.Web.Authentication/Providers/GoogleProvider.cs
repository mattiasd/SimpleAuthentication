﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Net;
using RestSharp;
using WorldDomination.Web.Authentication.Providers.Google;
using WorldDomination.Web.Authentication.Tracing;

namespace WorldDomination.Web.Authentication.Providers
{
    // REFERENCE: https://developers.google.com/accounts/docs/OAuth2Login

    public class GoogleProvider : BaseOAuth20Provider
    {
        private const string ScopeKey = "&scope={0}";
        private const string AccessTokenKey = "access_token";
        private const string ExpiresInKey = "expires_in";
        private const string TokenTypeKey = "token_type";

        private readonly string _clientId;
        private readonly string _clientSecret;
        private readonly IList<string> _scope;

        public GoogleProvider(ProviderParams providerParams)
        {
            providerParams.Validate();

            _clientId = providerParams.Key;
            _clientSecret = providerParams.Secret;

            // Optionals.
            _scope = new List<string>
            {
                "https://www.googleapis.com/auth/userinfo.profile",
                "https://www.googleapis.com/auth/userinfo.email"
            };
        }

        #region Implementation of IAuthenticationProvider

        public override string Name
        {
            get { return "Google"; }
        }

        public override Uri RedirectToAuthenticate(IAuthenticationServiceSettings authenticationServiceSettings)
        {
            if (authenticationServiceSettings == null)
            {
                throw new ArgumentNullException("authenticationServiceSettings");
            }

            if (authenticationServiceSettings.CallBackUri == null)
            {
                throw new ArgumentException("authenticationServiceSettings.CallBackUri");
            }

            // Do we have any scope options?
            // NOTE: Google uses a space-delimeted string for their scope key.
            var scope = (_scope != null && _scope.Count > 0)
                            ? string.Format(ScopeKey, string.Join(" ", _scope))
                            : string.Empty;

            var state = string.IsNullOrEmpty(authenticationServiceSettings.State)
                            ? string.Empty
                            : "&state=" + authenticationServiceSettings.State;

            var oauthDialogUri =
                string.Format(
                    "https://accounts.google.com/o/oauth2/auth?client_id={0}&redirect_uri={1}&response_type=code{2}{3}",
                    _clientId, authenticationServiceSettings.CallBackUri.AbsoluteUri, state, scope);

            return new Uri(oauthDialogUri);
        }

        public override IAuthenticationServiceSettings DefaultAuthenticationServiceSettings
        {
            get { return new GoogleAuthenticationServiceSettings(); }
        }

        protected override TraceSource TraceSource
        {
            get { return TraceManager["WD.Web.Authentication.Providers." + Name]; }
        }

        #endregion

        #region BaseOAuth20Provider Members

        protected override string RetrieveAuthorizationCode(NameValueCollection queryStringParameters, string existingState = null)
        {
            if (queryStringParameters == null)
            {
                throw new ArgumentNullException("queryStringParameters");
            }

            if (queryStringParameters.Count <= 0)
            {
                throw new ArgumentOutOfRangeException("queryStringParameters");
            }

            /* Documentation:
               Google returns an authorization code to your application if the user grants your application the permissions it requested. 
               The authorization code is returned to your application in the query string parameter code. If the state parameter was included in the request,
               then it is also included in the response. */
            var code = queryStringParameters["code"];
            var error = queryStringParameters["error"];

            // First check for any errors.
            if (!string.IsNullOrEmpty(error))
            {
                var errorMessage = "Failed to retrieve an authorization code from Google. The error provided is: " + error;
                TraceSource.TraceError(errorMessage);
                throw new AuthenticationException(errorMessage);
            }

            // Otherwise, we need a code.
            if (string.IsNullOrEmpty(code))
            {
                const string errorMessage = "No code parameter provided in the response query string from Google.";
                TraceSource.TraceError(errorMessage);
                throw new AuthenticationException(errorMessage);
            }

            return code;
        }

        protected override AccessToken RetrieveAccessToken(string authorizationCode, Uri redirectUri)
        {
            if (string.IsNullOrEmpty(authorizationCode))
            {
                throw new ArgumentNullException("authorizationCode");
            }

            if (redirectUri == null ||
                string.IsNullOrEmpty(redirectUri.AbsoluteUri))
            {
                throw new ArgumentNullException("redirectUri");
            }

            IRestResponse<AccessTokenResult> response;

            try
            {
                var restRequest = new RestRequest("/o/oauth2/token", Method.POST);
                restRequest.AddParameter("client_id", _clientId);
                restRequest.AddParameter("client_secret", _clientSecret);
                restRequest.AddParameter("redirect_uri", redirectUri.AbsoluteUri);
                restRequest.AddParameter("code", authorizationCode);
                restRequest.AddParameter("grant_type", "authorization_code");
                
                var restClient = RestClientFactory.CreateRestClient("https://accounts.google.com");
                TraceSource.TraceVerbose("Retrieving Access Token endpoint: {0}",
                                         restClient.BuildUri(restRequest).AbsoluteUri);

                response = restClient.Execute<AccessTokenResult>(restRequest);
            }
            catch (Exception exception)
            {
                var errorMessage =
                    string.Format("Failed to retrieve an oauth access token from Google. Error Messages: {0}",
                                  exception.RecursiveErrorMessages());
                TraceSource.TraceError(errorMessage);
                throw new AuthenticationException(errorMessage, exception);
            }

            if (response == null ||
                response.StatusCode != HttpStatusCode.OK)
            {
                var errorMessage = string.Format(
                    "Failed to obtain an Access Token from Facebook OR the the response was not an HTTP Status 200 OK. Response Status: {0}. Response Description: {1}. Error Content: {2}. Error Message: {3}.",
                    response == null ? "-- null response --" : response.StatusCode.ToString(),
                    response == null ? string.Empty : response.StatusDescription,
                    response == null ? string.Empty : response.Content,
                    response == null
                        ? string.Empty
                        : response.ErrorException == null
                              ? "--no error exception--"
                              : response.ErrorException.RecursiveErrorMessages());

                TraceSource.TraceError(errorMessage);
                throw new AuthenticationException(errorMessage);
            }

            // Grab the params which should have the request token info.
            if (string.IsNullOrEmpty(response.Data.AccessToken) ||
                response.Data.ExpiresIn <= 0 ||
                string.IsNullOrEmpty(response.Data.TokenType))
            {
                var errorMessage = string.Format( "Retrieved a Google Access Token but it doesn't contain one or more of either: {0}, {1} or {2}.",
                                   AccessTokenKey, ExpiresInKey, TokenTypeKey);
                TraceSource.TraceError(errorMessage);
                ;
                throw new AuthenticationException(errorMessage);
            }

            return new AccessToken
                   {
                       PublicToken = response.Data.AccessToken,
                       ExpiresOn = DateTime.UtcNow.AddSeconds(response.Data.ExpiresIn)
                   };
        }

        protected override UserInformation RetrieveUserInformation(AccessToken accessToken)
        {
            if (accessToken == null)
            {
                throw new ArgumentNullException("accessToken");
            }

            if (string.IsNullOrEmpty(accessToken.PublicToken))
            {
                throw new ArgumentException("accessToken.PublicToken");
            }

            IRestResponse<UserInfoResult> response;

            try
            {
                var restRequest = new RestRequest("/oauth2/v2/userinfo", Method.GET);
                restRequest.AddParameter(AccessTokenKey, accessToken.PublicToken);

                var restClient = RestClientFactory.CreateRestClient("https://www.googleapis.com");

                TraceSource.TraceVerbose("Retrieving user information. Google Endpoint: {0}",
                                         restClient.BuildUri(restRequest).AbsoluteUri);

                response = restClient.Execute<UserInfoResult>(restRequest);
            }
            catch (Exception exception)
            {
                var errorMessage = string.Format("Failed to retrieve any UserInfo data from the Google Api. Error Messages: {0}",
                                  exception.RecursiveErrorMessages());
                TraceSource.TraceError(errorMessage);
                throw new AuthenticationException(errorMessage, exception);
            }

            if (response == null ||
                response.StatusCode != HttpStatusCode.OK)
            {
                var errorMessage = string.Format(
                    "Failed to obtain some UserInfo data from the Google Api OR the the response was not an HTTP Status 200 OK. Response Status: {0}. Response Description: {1}. Error Message: {2}.",
                    response == null ? "-- null response --" : response.StatusCode.ToString(),
                    response == null ? string.Empty : response.StatusDescription,
                    response == null
                        ? string.Empty
                        : response.ErrorException == null
                              ? "--no error exception--"
                              : response.ErrorException.RecursiveErrorMessages());

                TraceSource.TraceError(errorMessage);
                throw new AuthenticationException(errorMessage);
            }

            // Lets check to make sure we have some bare minimum data.
            if (string.IsNullOrEmpty(response.Data.Id))
            {
                const string errorMessage =
                    "We were unable to retrieve the User Id from Google API, the user may have denied the authorization.";
                TraceSource.TraceError(errorMessage);
                throw new AuthenticationException(errorMessage);
            }

            return new UserInformation
                   {
                       Id = response.Data.Id,
                       Gender = string.IsNullOrEmpty(response.Data.Gender)
                                    ? GenderType.Unknown
                                    : GenderTypeHelpers.ToGenderType(response.Data.Gender),
                       Name = response.Data.Name,
                       Email = response.Data.Email,
                       Locale = response.Data.Locale,
                       Picture = response.Data.Picture,
                       UserName = response.Data.GivenName
                   };
        }

        #endregion
    }
}