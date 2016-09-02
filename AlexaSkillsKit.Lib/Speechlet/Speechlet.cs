//  Copyright 2015 Stefan Negritoiu (FreeBusy). See LICENSE file for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;
using AlexaSkillsKit.Authentication;
using AlexaSkillsKit.Json;

namespace AlexaSkillsKit.Speechlet
{
    public abstract class Speechlet : ISpeechlet
    {
        /// <summary>
        /// Processes Alexa request AND validates request signature
        /// </summary>
        /// <returns></returns>
        public virtual string GetResponse(Dictionary<string, string[]> Headers, byte[] Content, out HttpStatusCode HttpStatusCode)
        {
            SpeechletRequestValidationResult validationResult = SpeechletRequestValidationResult.OK;
            DateTime now = DateTime.UtcNow; // reference time for this request

            string chainUrl = null;
            if (!Headers.ContainsKey(Sdk.SIGNATURE_CERT_URL_REQUEST_HEADER) ||
                String.IsNullOrEmpty(chainUrl = Headers[Sdk.SIGNATURE_CERT_URL_REQUEST_HEADER].First()))
            {
                validationResult = validationResult | SpeechletRequestValidationResult.NoCertHeader;
            }

            string signature = null;
            if (!Headers.ContainsKey(Sdk.SIGNATURE_REQUEST_HEADER) ||
                String.IsNullOrEmpty(signature = Headers[Sdk.SIGNATURE_REQUEST_HEADER].First()))
            {
                validationResult = validationResult | SpeechletRequestValidationResult.NoSignatureHeader;
            }

            // attempt to verify signature only if we were able to locate certificate and signature headers
            if (validationResult == SpeechletRequestValidationResult.OK)
            {
                if (!SpeechletRequestSignatureVerifier.VerifyRequestSignature(Content, signature, chainUrl))
                {
                    validationResult = validationResult | SpeechletRequestValidationResult.InvalidSignature;
                }
            }

            SpeechletRequestEnvelope alexaRequest = null;
            try
            {
                var alexaContent = UTF8Encoding.UTF8.GetString(Content);
                alexaRequest = SpeechletRequestEnvelope.FromJson(alexaContent);
            }
            catch (Newtonsoft.Json.JsonReaderException)
            {
                validationResult = validationResult | SpeechletRequestValidationResult.InvalidJson;
            }
            catch (InvalidCastException)
            {
                validationResult = validationResult | SpeechletRequestValidationResult.InvalidJson;
            }

            // attempt to verify timestamp only if we were able to parse request body
            if (alexaRequest != null)
            {
                if (!SpeechletRequestTimestampVerifier.VerifyRequestTimestamp(alexaRequest, now))
                {
                    validationResult = validationResult | SpeechletRequestValidationResult.InvalidTimestamp;
                }
            }

            if (alexaRequest == null || !OnRequestValidation(validationResult, now, alexaRequest))
            {
                HttpStatusCode = HttpStatusCode.BadRequest;
                return validationResult.ToString();
            }

            string alexaResponse = DoProcessRequest(alexaRequest);

            if (alexaResponse == null)
            {
                HttpStatusCode = HttpStatusCode.InternalServerError;
                return null;
            }
            else
            {
                HttpStatusCode = HttpStatusCode.OK;
                return alexaResponse;
            }
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="requestContent"></param>
        /// <returns></returns>
        public virtual string ProcessRequest(string requestContent)
        {
            var requestEnvelope = SpeechletRequestEnvelope.FromJson(requestContent);
            return DoProcessRequest(requestEnvelope);
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="requestJson"></param>
        /// <returns></returns>
        public virtual string ProcessRequest(JObject requestJson)
        {
            var requestEnvelope = SpeechletRequestEnvelope.FromJson(requestJson);
            return DoProcessRequest(requestEnvelope);
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="requestEnvelope"></param>
        /// <returns></returns>
        private string DoProcessRequest(SpeechletRequestEnvelope requestEnvelope)
        {
            Session session = requestEnvelope.Session;
            SpeechletResponse response = null;

            // process launch request
            if (requestEnvelope.Request is LaunchRequest)
            {
                var request = requestEnvelope.Request as LaunchRequest;
                if (requestEnvelope.Session.IsNew)
                {
                    OnSessionStarted(
                        new SessionStartedRequest(request.RequestId, request.Timestamp), session);
                }
                response = OnLaunch(request, session);
            }

            // process intent request
            else if (requestEnvelope.Request is IntentRequest)
            {
                var request = requestEnvelope.Request as IntentRequest;

                // Do session management prior to calling OnSessionStarted and OnIntentAsync 
                // to allow dev to change session values if behavior is not desired
                DoSessionManagement(request, session);

                if (requestEnvelope.Session.IsNew)
                {
                    OnSessionStarted(
                        new SessionStartedRequest(request.RequestId, request.Timestamp), session);
                }
                response = OnIntent(request, session);
            }

            // process session ended request
            else if (requestEnvelope.Request is SessionEndedRequest)
            {
                var request = requestEnvelope.Request as SessionEndedRequest;
                OnSessionEnded(request, session);
            }

            var responseEnvelope = new SpeechletResponseEnvelope
            {
                Version = requestEnvelope.Version,
                Response = response,
                SessionAttributes = requestEnvelope.Session.Attributes
            };
            return responseEnvelope.ToJson();
        }


        /// <summary>
        /// 
        /// </summary>
        private void DoSessionManagement(IntentRequest request, Session session)
        {
            if (session.IsNew)
            {
                session.Attributes[Session.INTENT_SEQUENCE] = request.Intent.Name;
            }
            else
            {
                // if the session was started as a result of a launch request 
                // a first intent isn't yet set, so set it to the current intent
                if (!session.Attributes.ContainsKey(Session.INTENT_SEQUENCE))
                {
                    session.Attributes[Session.INTENT_SEQUENCE] = request.Intent.Name;
                }
                else
                {
                    session.Attributes[Session.INTENT_SEQUENCE] += Session.SEPARATOR + request.Intent.Name;
                }
            }

            // Auto-session management: copy all slot values from current intent into session
            foreach (var slot in request.Intent.Slots.Values)
            {
                session.Attributes[slot.Name] = slot.Value;
            }
        }


        /// <summary>
        /// Opportunity to set policy for handling requests with invalid signatures and/or timestamps
        /// </summary>
        /// <returns>true if request processing should continue, otherwise false</returns>
        public virtual bool OnRequestValidation(
            SpeechletRequestValidationResult result, DateTime referenceTimeUtc, SpeechletRequestEnvelope requestEnvelope)
        {

            return result == SpeechletRequestValidationResult.OK;
        }


        public abstract SpeechletResponse OnIntent(IntentRequest intentRequest, Session session);
        public abstract SpeechletResponse OnLaunch(LaunchRequest launchRequest, Session session);
        public abstract void OnSessionStarted(SessionStartedRequest sessionStartedRequest, Session session);
        public abstract void OnSessionEnded(SessionEndedRequest sessionEndedRequest, Session session);
    }
}
