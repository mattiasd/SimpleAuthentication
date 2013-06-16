﻿using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Net;
using RestSharp;
using RestSharp.Authenticators;
using RestSharp.Contrib;
using WorldDomination.Web.Authentication.Providers.Twitter;
using WorldDomination.Web.Authentication.Tracing;

namespace WorldDomination.Web.Authentication.Providers
{
    public class TwitterProvider : BaseRestFactoryProvider, IAuthenticationProvider
    {
        private const string BaseUrl = "https://api.twitter.com";
        private const string DeniedKey = "denied";
        private const string OAuthTokenKey = "oauth_token";
        private const string OAuthTokenSecretKey = "oauth_token_secret";
        private const string OAuthVerifierKey = "oauth_verifier";

        private readonly string _consumerKey;
        private readonly string _consumerSecret;

        public TwitterProvider(ProviderParams providerParams)
        {
            providerParams.Validate();

            _consumerKey = providerParams.Key;
            _consumerSecret = providerParams.Secret;
        }

        private RequestTokenResult RetrieveRequestToken(IAuthenticationServiceSettings authenticationServiceSettings)
        {
            TraceSource.TraceVerbose("Retrieving the Request Token.");

            if (authenticationServiceSettings == null)
            {
                throw new ArgumentNullException("authenticationServiceSettings");
            }

            if (authenticationServiceSettings.CallBackUri == null ||
                string.IsNullOrEmpty(authenticationServiceSettings.CallBackUri.AbsoluteUri))
            {
                throw new ArgumentException("AuthenticationServiceSettings.CallBackUri");
            }

            if (string.IsNullOrEmpty(authenticationServiceSettings.State))
            {
                throw new ArgumentException("AuthenticationServiceSettings.State");
            }

            IRestResponse response;
            var callBackUri = string.Format("{0}&state={1}", authenticationServiceSettings.CallBackUri,
                                            authenticationServiceSettings.State);

            try
            {
                var restClient = RestClientFactory.CreateRestClient(BaseUrl);
                restClient.Authenticator = OAuth1Authenticator.ForRequestToken(_consumerKey, _consumerSecret, callBackUri);
                var restRequest = new RestRequest("oauth/request_token", Method.POST);

                TraceSource.TraceVerbose("Retrieving user information. Twitter Endpoint: {0}",
                                        restClient.BuildUri(restRequest).AbsoluteUri);

                response = restClient.Execute(restRequest);
            }
            catch (Exception exception)
            {
                throw new AuthenticationException("Failed to obtain a Request Token from Twitter.", exception);
            }

            if (response == null ||
                response.StatusCode != HttpStatusCode.OK)
            {
                var errorMessage = string.Format(
                    "Failed to obtain a request token from the Twitter api OR the the response was not an HTTP Status 200 OK. Response Status: {0}. Response Description: {1}. Error Message: {2}.",
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

            // Grab the params which should have the request token info.
            var querystringParameters = HttpUtility.ParseQueryString(response.Content);
            var oAuthToken = querystringParameters[OAuthTokenKey];
            var oAuthTokenSecret = querystringParameters[OAuthTokenSecretKey];

            TraceSource.TraceInformation("Retrieved OAuth Token: {0}. OAuth Verifier: {1}.",
                                         string.IsNullOrEmpty(oAuthToken) ? "--no token--" : oAuthToken,
                                         string.IsNullOrEmpty(oAuthTokenSecret) ? "--no secret--" : oAuthTokenSecret);

            if (string.IsNullOrEmpty(oAuthToken) ||
                string.IsNullOrEmpty(oAuthTokenSecret))
            {
                throw new AuthenticationException(
                    "Retrieved a Twitter Request Token but it doesn't contain both the oauth_token and oauth_token_secret parameters.");
            }

            TraceSource.TraceVerbose("OAuth Token retrieved.");

            return new RequestTokenResult
            {
                OAuthToken = oAuthToken,
                OAuthTokenSecret = oAuthTokenSecret
            };
        }

        private VerifierResult RetrieveOAuthVerifier(NameValueCollection queryStringParameters)
        {
            TraceSource.TraceVerbose("Retrieving the OAuth Verifier.");

            if (queryStringParameters == null)
            {
                throw new ArgumentNullException("queryStringParameters");
            }

            if (queryStringParameters.Count <= 0)
            {
                throw new ArgumentOutOfRangeException("queryStringParameters");
            }

            var denied = queryStringParameters[DeniedKey];
            if (!string.IsNullOrEmpty(denied))
            {
                throw new AuthenticationException(
                    "Failed to accept the Twitter App Authorization. Therefore, authentication didn't proceed.");
            }

            var oAuthToken = queryStringParameters[OAuthTokenKey];
            var oAuthVerifier = queryStringParameters[OAuthVerifierKey];

            TraceSource.TraceInformation("Retrieved OAuth Token: {0}. OAuth Verifier: {1}.",
                                         string.IsNullOrEmpty(oAuthToken) ? "--no token--" : oAuthToken,
                                         string.IsNullOrEmpty(oAuthVerifier) ? "--no verifier--" : oAuthVerifier);

            if (string.IsNullOrEmpty(oAuthToken) ||
                string.IsNullOrEmpty(oAuthVerifier))
            {
                throw new AuthenticationException(
                    "Failed to retrieve an oauth_token and an oauth_token_secret after the client has signed and approved via Twitter.");
            }

            TraceSource.TraceVerbose("OAuth Verifier retrieved.");

            return new VerifierResult
            {
                OAuthToken = oAuthToken,
                OAuthVerifier = oAuthVerifier
            };
        }

        private AccessTokenResult RetrieveAccessToken(VerifierResult verifierResult)
        {
            if (verifierResult == null)
            {
                throw new ArgumentNullException("verifierResult");
            }

            if (string.IsNullOrEmpty(verifierResult.OAuthToken))
            {
                throw new ArgumentException("verifierResult.OAuthToken");
            }

            if (string.IsNullOrEmpty(verifierResult.OAuthToken))
            {
                throw new ArgumentException("verifierResult.OAuthVerifier");
            }

            IRestResponse response;
            try
            {
                var restRequest = new RestRequest("oauth/access_token", Method.POST);
                var restClient = RestClientFactory.CreateRestClient(BaseUrl);
                TraceSource.TraceVerbose("Retrieving Access Token endpoint: {0}",
                                         restClient.BuildUri(restRequest).AbsoluteUri);

                restClient.Authenticator = OAuth1Authenticator.ForAccessToken(_consumerKey, _consumerSecret,
                                                                               verifierResult.OAuthToken,
                                                                               null, verifierResult.OAuthVerifier);
                response = restClient.Execute(restRequest);
            }
            catch (Exception exception)
            {
                var errorMessage =
                    string.Format("Failed to retrieve an oauth access token from Twitter. Error Messages: {0}",
                                  exception.RecursiveErrorMessages());
                TraceSource.TraceError(errorMessage);
                throw new AuthenticationException(errorMessage, exception);
            }

            if (response == null || 
                response.StatusCode != HttpStatusCode.OK)
            {
                var errorMessage = string.Format(
                    "Failed to obtain an Access Token from Twitter OR the the response was not an HTTP Status 200 OK. Response Status: {0}. Response Description: {1}. Error Content: {2}. Error Message: {3}.",
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

            var querystringParameters = HttpUtility.ParseQueryString(response.Content);

            TraceSource.TraceVerbose("Retrieved OAuth Token - Public Key: {0}. Secret Key: {1} ",
                                     string.IsNullOrEmpty(querystringParameters[OAuthTokenKey])
                                         ? "no public key retrieved from the querystring. What Ze Fook?"
                                         : querystringParameters[OAuthTokenKey],
                                     string.IsNullOrEmpty(querystringParameters[OAuthTokenSecretKey])
                                         ? "no secret key retrieved from the querystring. What Ze Fook?"
                                         : querystringParameters[OAuthTokenSecretKey]);

            return new AccessTokenResult
            {
                AccessToken = querystringParameters[OAuthTokenKey],
                AccessTokenSecret = querystringParameters[OAuthTokenSecretKey]
            };
        }

        private VerifyCredentialsResult VerifyCredentials(AccessTokenResult accessTokenResult)
        {
            if (accessTokenResult == null)
            {
                throw new ArgumentNullException("accessTokenResult");
            }

            if (string.IsNullOrEmpty(accessTokenResult.AccessToken))
            {
                throw new ArgumentException("accessTokenResult.AccessToken");
            }

            if (string.IsNullOrEmpty(accessTokenResult.AccessTokenSecret))
            {
                throw new ArgumentException("accessTokenResult.AccessTokenSecret");
            }

            IRestResponse<VerifyCredentialsResult> response;
            try
            {
                var restClient = RestClientFactory.CreateRestClient(BaseUrl);
                restClient.Authenticator = OAuth1Authenticator.ForProtectedResource(_consumerKey, _consumerSecret,
                                                                                     accessTokenResult.AccessToken,
                                                                                     accessTokenResult.AccessTokenSecret);
                var restRequest = new RestRequest("1.1/account/verify_credentials.json");

                TraceSource.TraceVerbose("Retrieving user information. Twitter Endpoint: {0}",
                                        restClient.BuildUri(restRequest).AbsoluteUri);

                response = restClient.Execute<VerifyCredentialsResult>(restRequest);
            }
            catch (Exception exception)
            {
                var errorMessage = "Failed to retrieve VerifyCredentials json data from the Twitter Api. Error Messages: "
                                   + exception.RecursiveErrorMessages();
                TraceSource.TraceError(errorMessage);
                throw new AuthenticationException(errorMessage, exception);
            }

            if (response == null ||
                response.StatusCode != HttpStatusCode.OK ||
                response.Data == null)
            {
                var errorMessage = string.Format(
                    "Failed to obtain some VerifyCredentials json data from the Facebook api OR the the response was not an HTTP Status 200 OK. Response Status: {0}. Response Description: {1}. Error Message: {2}.",
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

            return response.Data;
        }

        #region Implementation of IAuthenticationProvider

        public string Name
        {
            get { return "Twitter"; }
        }

        public Uri RedirectToAuthenticate(IAuthenticationServiceSettings authenticationServiceSettings)
        {
            if (authenticationServiceSettings == null)
            {
                throw new ArgumentNullException("authenticationServiceSettings");
            }

            if (authenticationServiceSettings.CallBackUri == null)
            {
                throw new ArgumentException("authenticationServiceSettings.CallBackUri");
            }

            // First we need to grab a request token.
            var oAuthToken = RetrieveRequestToken(authenticationServiceSettings);

            // Now we need the user to enter their name/password/accept this app @ Twitter.
            // This means we need to redirect them to the Twitter website.
            var request = new RestRequest("oauth/authenticate");
            request.AddParameter(OAuthTokenKey, oAuthToken.OAuthToken);
            var restClient = RestClientFactory.CreateRestClient(BaseUrl);
            return restClient.BuildUri(request);
        }

        public IAuthenticatedClient AuthenticateClient(IAuthenticationServiceSettings authenticationServiceSettings,
                                                       NameValueCollection queryStringParameters)
        {
            TraceSource.TraceVerbose("Trying to get the authenticated client details. NOTE: This is using OAuth 1.0a. ~~Le sigh~~.");

            if (authenticationServiceSettings == null)
            {
                throw new ArgumentNullException("authenticationServiceSettings");
            }

            // Retrieve the OAuth Verifier.
            var oAuthVerifier = RetrieveOAuthVerifier(queryStringParameters);

            // Convert the Request Token to an Access Token, now that we have a verifier.
            var oAuthAccessToken = RetrieveAccessToken(oAuthVerifier);

            // Grab the user information.
            var verifyCredentialsResult = VerifyCredentials(oAuthAccessToken);

            return new AuthenticatedClient(Name.ToLowerInvariant())
            {
                UserInformation = new UserInformation
                {
                    Name = verifyCredentialsResult.Name,
                    Id = verifyCredentialsResult.Id.ToString(),
                    Locale = verifyCredentialsResult.Lang,
                    UserName = verifyCredentialsResult.ScreenName,
                    Picture = verifyCredentialsResult.ProfileImageUrl
                },
                AccessToken = new AccessToken
                              {
                                  PublicToken = oAuthAccessToken.AccessToken
                              }
            };
        }

        public IAuthenticationServiceSettings DefaultAuthenticationServiceSettings
        {
            get { return new TwitterAuthenticationServiceSettings(); }
        }

        protected override TraceSource TraceSource
        {
            get { return TraceManager["WD.Web.Authentication.Providers." + Name]; }
        }

        #endregion
    }
}