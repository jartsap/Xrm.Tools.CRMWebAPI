// =====================================================================
//  File:		CRMWebAPI
//  Summary:	Helper library for working with CRM Web API
// =====================================================================
// 
//  THIS CODE AND INFORMATION ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY
//  KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
//  IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A
//  PARTICULAR PURPOSE.
// 
//  Any use or other rights related to this source code, resulting object code or 
//  related artifacts are controlled the prevailing EULA in effect. See the EULA
//  for detail rights. In the event no EULA was provided contact copyright holder
//  for a current copy.
//
// =====================================================================
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Xrm.Tools.WebAPI.Requests;
using Xrm.Tools.WebAPI.Results;
using System.Net.Http.Formatting;
using System.Xml;
using System.Web;

namespace Xrm.Tools.WebAPI
{
    public class CRMWebAPI
    {
        private HttpClient _httpClient = null;
        private CRMWebAPIConfig _crmWebAPIConfig;

        /// <summary>
        /// Instaciate the CRMWebAPI using the CRMWebAPIConfig, if NetworkCredentials are present it is assumed a on-premisse connection type.
        /// </summary>
        /// <param name="crmWebAPIConfig"> Api Config Object, it contais the  </param>
        public CRMWebAPI(CRMWebAPIConfig crmWebAPIConfig)
        {
            _crmWebAPIConfig = crmWebAPIConfig;

            if (_crmWebAPIConfig.NetworkCredential != null)
                _httpClient = new HttpClient(new HttpClientHandler { Credentials = _crmWebAPIConfig.NetworkCredential });
            else
            {
                _httpClient = new HttpClient();
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _crmWebAPIConfig.AccessToken);
            }

            SetHttpClientDefaults(_crmWebAPIConfig.CallerID);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="apiUrl">CRM API base URL e.g. https://orgname.api.crm.dynamics.com/api/data/v8.0/ </param>
        /// <param name="accessToken">allows for hard coded access token for testing</param>
        /// <param name="callerID">user id to impersonate on calls</param>
        /// <param name="getAccessToken">method to call to refresh access token, called before each use of token</param>
        public CRMWebAPI(string apiUrl, string accessToken, Guid callerID = default(Guid), Func<string, Task<string>> getAccessToken = null)
        {
            _crmWebAPIConfig = new CRMWebAPIConfig
            {
                APIUrl = apiUrl,
                AccessToken = accessToken,
                CallerID = callerID,
                GetAccessToken = getAccessToken
            };

            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _crmWebAPIConfig.AccessToken);
            SetHttpClientDefaults(callerID);
        }

        /// <summary>
        /// On-premise Active Directory with Credentials
        /// </summary>
        /// <param name="apiUrl"></param>
        /// <param name="networkCredential"></param>
        public CRMWebAPI(string apiUrl, NetworkCredential networkCredential = null, Guid callerID = default(Guid))
        {
            _crmWebAPIConfig = new CRMWebAPIConfig
            {
                APIUrl = apiUrl,
                NetworkCredential = networkCredential,
                CallerID = callerID
            };

            if (_crmWebAPIConfig.NetworkCredential != null)
                _httpClient = new HttpClient(new HttpClientHandler { Credentials = networkCredential });
            else
                _httpClient = new HttpClient();

            SetHttpClientDefaults(callerID);
        }

        /// <summary>
        /// Retrieve a list of records based on query options
        /// </summary>
        /// <param name="uri">e.g. accounts</param>
        /// <param name="QueryOptions">Filter, OrderBy,Select, and other options</param>
        /// <returns></returns>
        public async Task<CRMGetListResult<ExpandoObject>> GetList(string uri, CRMGetListOptions QueryOptions = null)
        {
            await CheckAuthToken();

            string fullUrl = BuildGetUrl(uri, QueryOptions);
            HttpRequestMessage request = new HttpRequestMessage(new HttpMethod("GET"), fullUrl);
            FillPreferHeader(request, QueryOptions);

            var results = await _httpClient.SendAsync(request);

            EnsureSuccessStatusCode(results);
            var data = await results.Content.ReadAsStringAsync();
            CRMGetListResult<ExpandoObject> resultList = new CRMGetListResult<ExpandoObject>();
            resultList.List = new List<ExpandoObject>();

            var values = JObject.Parse(data);
            var valueList = values["value"].ToList();
            foreach (var value in valueList)
            {
                if (_crmWebAPIConfig.ResolveUnicodeNames)
                    FormatResultProperties((JObject)value);
                resultList.List.Add(value.ToObject<ExpandoObject>());
            }

            var deltaLink = values["@odata.deltaLink"];
            if (deltaLink != null)
                resultList.TrackChangesLink = deltaLink.ToString();
            var recordCount = values["@odata.count"];
            if (recordCount != null)
                resultList.Count = int.Parse(recordCount.ToString());
            var nextLink = values["@odata.nextLink"];
            while (nextLink != null)
            {
                var nextLinkUri = new Uri((string)nextLink);
                string pathQuery = nextLinkUri.PathAndQuery;
                string host = nextLink.ToString().Replace(pathQuery, "");
                var apiUri = new Uri(_crmWebAPIConfig.APIUrl);
                string apiQuery = apiUri.PathAndQuery;
                string apiHost = _crmWebAPIConfig.APIUrl.ToString().Replace(apiQuery, "");
                nextLink = ((string)nextLink).Replace(host, apiHost);

                var nextResults = await _httpClient.GetAsync(nextLink.ToString());
                EnsureSuccessStatusCode(nextResults);
                var nextData = await nextResults.Content.ReadAsStringAsync();

                var nextValues = JObject.Parse(nextData);
                var nextValueList = nextValues["value"].ToList();
                foreach (var nextvalue in nextValueList)
                    resultList.List.Add(nextvalue.ToObject<ExpandoObject>());

                var nextDeltaLink = nextValues["@odata.deltaLink"];
                if (nextDeltaLink != null)
                    resultList.TrackChangesLink = nextDeltaLink.ToString();

                nextLink = nextValues["@odata.nextLink"];
            }

            return resultList;
        }
        public async Task<List<ResultType>> GetList<ResultType>(string uri, string fetchXml, int pageSize = 500)
        {
            var results = new List<ResultType>();
            string pagingCookie = null;
            int page = 1;
            do
            {
                string xml = CreateXml(fetchXml, null, page, pageSize);
                var res = await this.GetListPage<ResultType>(uri, xml);
                results.AddRange(res.List);
                pagingCookie = res.PagingCookie;
                if (pagingCookie != null && pagingCookie != "")
                {
                    pagingCookie = HttpUtility.HtmlEncode((HttpUtility.HtmlEncode(pagingCookie)));
                    pagingCookie = pagingCookie.Replace("&", "%26");

                }
                else pagingCookie = null;
                page++;
            } while (!String.IsNullOrEmpty(pagingCookie));
            return results;
        }

        private  async Task<CRMGetListFetchXmlResult<ResultType>> GetListPage<ResultType>(string uri, string fetchXml)
        {
            var boundary = "fetchdata";
            await CheckAuthToken();

            string fullUrl = BuildGetUrl(uri, null);
            string batchUrl = _crmWebAPIConfig.APIUrl + "$batch";
            HttpRequestMessage request = new HttpRequestMessage(new HttpMethod("Post"), batchUrl);
            request.Headers.Add("OData-MaxVersion", "4.0");
            request.Headers.Add("OData-Version", "4.0");
            request.Headers.Add("Accept", "application/json");
            
           
            var body = "--"+ boundary + "\n" +
                  "Content-Type: application/http\n" +
                  "Content-Transfer-Encoding: binary\n" +
                  "\n" +
                  "GET " +
                  fullUrl +
                  "?fetchXml=" +
                  Uri.EscapeUriString(fetchXml) +
                  " HTTP/1.1\n" +
                  //@"Prefer: odata.include-annotations=""*""\n" +
                  "\n" +
                  "--"+boundary+"--";

            var content = new StringContent(body,Encoding.UTF8, "multipart/mixed");
            content.Headers.Remove("Content-Type");
            content.Headers.TryAddWithoutValidation("Content-Type", "multipart/mixed; boundary=" + boundary);
            request.Content = content;
            var results = await _httpClient.SendAsync(request);
            
            EnsureSuccessStatusCode(results);
            
            var multipart = await results.Content.ReadAsMultipartAsync();

            var resultList = new CRMGetListFetchXmlResult<ResultType>();
            resultList.List = new List<ResultType>();
            foreach (var part in multipart.Contents)
            {
                if (part.Headers.ContentType.MediaType == "application/http")
                {
                    part.Headers.ContentType.Parameters.Add(new NameValueHeaderValue("msgtype", "response"));
                    var inner = await part.ReadAsHttpResponseMessageAsync();
                    var data = await inner.Content.ReadAsStringAsync();
                    var values = JObject.Parse(data);
                    JToken cookie = null;
                    var ok = values.TryGetValue("@Microsoft.Dynamics.CRM.fetchxmlpagingcookie", out cookie);
                    //var recordCount = values["@odata.count"]; // This doesn't work for some unknown reason?
                    //var total = values[""];// This doesn't work for some unknown reason?
                    
                    if (ok) resultList.PagingCookie = cookie.Value<string>();
                    if (values["value"] == null) throw new KeyNotFoundException("'value' collection was not found in FetchXml response. Response: " + data);
                    foreach (var value in values["value"].ToList())
                    {
                        if (_crmWebAPIConfig.ResolveUnicodeNames)
                            FormatResultProperties((JObject)value);
                        resultList.List.Add(value.ToObject<ResultType>());
                    }
                }
            }
            return resultList;

            //var data = await results.Content.ReadAsStringAsync();
            //var values = JObject.Parse(data);
            //CRMGetListResult<ResultType> resultList = new CRMGetListResult<ResultType>();
            //resultList.List = new List<ResultType>();

            //foreach (var value in values["value"].ToList())
            //{
            //    if (_crmWebAPIConfig.ResolveUnicodeNames)
            //        FormatResultProperties((JObject)value);
            //    resultList.List.Add(value.ToObject<ResultType>());
            //}

            //var deltaLink = values["@odata.deltaLink"];
            //if (deltaLink != null)
            //    resultList.TrackChangesLink = deltaLink.ToString();


            //var recordCount = values["@odata.count"];
            //if (recordCount != null)
            //    resultList.Count = int.Parse(recordCount.ToString());

            //return resultList;
        }
        private string CreateXml(XmlDocument doc, string cookie, int page, int count)
        {
            XmlAttributeCollection attrs = doc.DocumentElement.Attributes;

            if (cookie != null)
            {
                XmlAttribute pagingAttr = doc.CreateAttribute("paging-cookie");
                pagingAttr.Value = cookie;
                attrs.Append(pagingAttr);
            }

            XmlAttribute pageAttr = doc.CreateAttribute("page");
            pageAttr.Value = System.Convert.ToString(page);
            attrs.Append(pageAttr);

            XmlAttribute countAttr = doc.CreateAttribute("count");
            countAttr.Value = System.Convert.ToString(count);
            attrs.Append(countAttr);

            StringBuilder sb = new StringBuilder(1024);
            StringWriter stringWriter = new StringWriter(sb);

            XmlTextWriter writer = new XmlTextWriter(stringWriter);
            doc.WriteTo(writer);
            writer.Close();

            return sb.ToString();
        }
        private string CreateXml(string xml, string cookie, int page, int count)
        {
            StringReader stringReader = new StringReader(xml);
            XmlTextReader reader = new XmlTextReader(stringReader);

            // Load document
            XmlDocument doc = new XmlDocument();
            doc.Load(reader);

            return CreateXml(doc, cookie, page, count);
        }
        public async Task<Tuple<CRMGetListResult<ExpandoObject>, string>> GetList(string uri, bool isFullUrl, int maxResultCount, CRMGetListOptions QueryOptions = null)
        {
            //Console.WriteLine("GetList URI: " + uri + "      ---   isFullUrl:" + isFullUrl);

            await CheckAuthToken();

            string fullUrl = isFullUrl ? uri : BuildGetUrl(uri, QueryOptions);
            fullUrl = fullUrl.Replace("http://", "https://"); //TODO: Clean up later
            HttpRequestMessage request = new HttpRequestMessage(new HttpMethod("GET"), fullUrl);

            var preferList = new List<string>();

            preferList.Add("odata.maxpagesize=" + maxResultCount);

            if ((QueryOptions != null) && (QueryOptions.FormattedValues))
                preferList.Add("odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\"");

            if ((QueryOptions != null) && (QueryOptions.TrackChanges))
                preferList.Add("odata.track-changes");

            if (preferList.Count > 0)
                request.Headers.Add("Prefer", string.Join(",", preferList));

            var results = await _httpClient.SendAsync(request);

            EnsureSuccessStatusCode(results);
            var data = await results.Content.ReadAsStringAsync();
            CRMGetListResult<ExpandoObject> resultList = new CRMGetListResult<ExpandoObject>();
            resultList.List = new List<ExpandoObject>();

            var values = JObject.Parse(data);
            var valueList = values["value"].ToList();
            foreach (var value in valueList)
            {
                if (value == null)
                {
                    Console.WriteLine("Empty value");
                }
                resultList.List.Add(value.ToObject<ExpandoObject>());
            }

            var nextLink = values["@odata.nextLink"] != null ? values["@odata.nextLink"].ToString() : null;


            if (!String.IsNullOrEmpty(nextLink))
            {
                var nextLinkUri = new Uri((string)nextLink);
                string pathQuery = nextLinkUri.PathAndQuery;
                string host = nextLink.ToString().Replace(pathQuery, "");
                var apiUri = new Uri(_crmWebAPIConfig.APIUrl);
                string apiQuery = apiUri.PathAndQuery;
                string apiHost = _crmWebAPIConfig.APIUrl.ToString().Replace(apiQuery, "");
                nextLink = ((string)nextLink).Replace(host, apiHost);
            }

            return new Tuple<CRMGetListResult<ExpandoObject>, string>(resultList, nextLink);

        }

        /// <summary>
        /// Retrieve a list of records based on query options
        /// </summary>
        /// <typeparam name="ResultType"></typeparam>
        /// <param name="uri">e.g. accounts</param>
        /// <param name="QueryOptions">Filter, OrderBy,Select, and other options</param>
        /// <returns></returns>
        public async Task<CRMGetListResult<ResultType>> GetList<ResultType>(string uri, CRMGetListOptions QueryOptions = null)
        {
            await CheckAuthToken();

            string fullUrl = BuildGetUrl(uri, QueryOptions);



            HttpRequestMessage request = new HttpRequestMessage(new HttpMethod("GET"), fullUrl);
            var preferList = new List<string>();
            preferList.Add("odata.maxpagesize=1000");

            if ((QueryOptions != null) && (QueryOptions.FormattedValues))
                preferList.Add("odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\"");

            if ((QueryOptions != null) && (QueryOptions.TrackChanges))
                preferList.Add("odata.track-changes");

            if (preferList.Count > 0)
                request.Headers.Add("Prefer", string.Join(",", preferList));

            //FillPreferHeader(request, QueryOptions);

            //if (QueryOptions != null && !String.IsNullOrEmpty(QueryOptions.FetchXml))
            //    request.Headers.Add("Prefer", "odata.include-annotations=\"Microsoft.Dynamics.CRM.*\"");



            var results = await _httpClient.SendAsync(request);

            EnsureSuccessStatusCode(results);
            var data = await results.Content.ReadAsStringAsync();
            var values = JObject.Parse(data);
            CRMGetListResult<ResultType> resultList = new CRMGetListResult<ResultType>();
            resultList.List = new List<ResultType>();

            foreach (var value in values["value"].ToList())
            {
                if (_crmWebAPIConfig.ResolveUnicodeNames)
                    FormatResultProperties((JObject)value);
                resultList.List.Add(value.ToObject<ResultType>());
            }

            var deltaLink = values["@odata.deltaLink"];
            if (deltaLink != null)
                resultList.TrackChangesLink = deltaLink.ToString();

            var nextLink = values["@odata.nextLink"];
            var recordCount = values["@odata.count"];
            if (recordCount != null)
                resultList.Count = int.Parse(recordCount.ToString());

            while (nextLink != null)
            {
                var nextLinkUri = new Uri((string)nextLink);
                string pathQuery = nextLinkUri.PathAndQuery;
                string host = nextLink.ToString().Replace(pathQuery, "");
                var apiUri = new Uri(_crmWebAPIConfig.APIUrl);
                string apiQuery = apiUri.PathAndQuery;
                string apiHost = _crmWebAPIConfig.APIUrl.ToString().Replace(apiQuery, "");
                nextLink = ((string)nextLink).Replace(host, apiHost);
                var nextResults = await _httpClient.GetAsync(nextLink.ToString());
                EnsureSuccessStatusCode(nextResults);
                var nextData = await nextResults.Content.ReadAsStringAsync();

                var nextValues = JObject.Parse(nextData);
                foreach (var value in nextValues["value"].ToList())
                {
                    resultList.List.Add(value.ToObject<ResultType>());
                }
                nextLink = nextValues["@odata.nextLink"];
            }
            return resultList;
        }

        /// <summary>
        /// Get count of matching records.  
        /// 
        /// Note: This returns up to 5,000 records matching criteria it will not reflect all records over 5,000 due to
        /// a limitiation with CRM internal handling of retrieval of the count
        /// </summary>
        /// <param name="uri">e.g. accounts</param>
        /// <param name="QueryOptions">Filter, OrderBy,Select, and other options</param>
        /// <returns></returns>
        public async Task<int> GetCount(string uri, CRMGetListOptions QueryOptions = null)
        {
            await CheckAuthToken();
            if (QueryOptions != null)
                QueryOptions.IncludeCount = false;
            string fullUrl = BuildGetUrl(uri + "/$count", QueryOptions);
            var results = await _httpClient.GetAsync(fullUrl);
            EnsureSuccessStatusCode(results);
            var data = await results.Content.ReadAsStringAsync();

            return int.Parse(data);

        }

        /// <summary>
        /// get a single record by entityID with the specified return type
        /// </summary>
        /// <param name="entityCollection"></param>
        /// <param name="entityID"></param>
        /// <param name="QueryOptions"></param>
        /// <returns>ExpandoObject</returns>
        public async Task<ExpandoObject> Get(string entityCollection, Guid entityID, CRMGetListOptions QueryOptions = null)
        {
            return await Get<ExpandoObject>(entityCollection, entityID.ToString(), QueryOptions);
        }

        /// <summary>
        /// get a single record by entityID with the specified return type
        /// </summary>
        /// <param name="entityCollection"></param>
        /// <param name="entityID"></param>
        /// <param name="QueryOptions"></param>
        /// <returns></returns>
        public async Task<ResultType> Get<ResultType>(string entityCollection, Guid entityID, CRMGetListOptions QueryOptions = null)
        {
            return await Get<ResultType>(entityCollection, entityID.ToString(), QueryOptions);
        }

        /// <summary>
        /// get a single record by alternate or entityID key with the specified return type
        /// </summary>
        /// <typeparam name="ResultType"></typeparam>
        /// <param name="entityCollection"></param>
        /// <param name="key">Alternate key or entity ID</param>
        /// <param name="QueryOptions"></param>
        /// <returns></returns>
        public async Task<ResultType> Get<ResultType>(string entityCollection, string key, CRMGetListOptions QueryOptions = null)
        {
            await CheckAuthToken();

            string fullUrl = string.Empty;
            if (key.Equals(Guid.Empty.ToString()) || String.IsNullOrEmpty(key))
                fullUrl = BuildGetUrl(entityCollection, QueryOptions);
            else
                fullUrl = BuildGetUrl(entityCollection + "(" + key + ")", QueryOptions);
            HttpRequestMessage request = new HttpRequestMessage(new HttpMethod("GET"), fullUrl);

            if ((QueryOptions != null) && (QueryOptions.FormattedValues))
                request.Headers.Add("Prefer", "odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\"");

            var results = await _httpClient.SendAsync(request);

            EnsureSuccessStatusCode(results);
            var data = await results.Content.ReadAsStringAsync();
            var value = JObject.Parse(data);
            if (_crmWebAPIConfig.ResolveUnicodeNames)
                FormatResultProperties(value);

            return value.ToObject<ResultType>();
        }

        /// <summary>
        /// create a new record
        /// </summary>
        /// <param name="entityCollection"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public async Task<Guid> Create(string entityCollection, object data)
        {
            await CheckAuthToken();

            var fullUrl = _crmWebAPIConfig.APIUrl + entityCollection;

            HttpRequestMessage request = new HttpRequestMessage(new HttpMethod("Post"), fullUrl);

            var jsonData = JsonConvert.SerializeObject(data);

            request.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);

            EnsureSuccessStatusCode(response, jsonData: jsonData);

            Guid idGuid = GetEntityIDFromResponse(response);

            return idGuid;
        }

        /// <summary>
        /// create multiple records at once using a batch
        /// </summary>
        /// <param name="entityCollection"></param>
        /// <param name="datalist"></param>
        /// <returns></returns>
        public async Task<CRMBatchResult> Create(string entityCollection, object[] datalist)
        {
            await CheckAuthToken();

#if WINDOWS_APP
     throw new NotImplementedException();
#elif NETCOREAPP1_0
            throw new NotImplementedException();
#elif NETCOREAPP2_1
            throw new NotImplementedException();
#elif NETSTANDARD1_4
            throw new NotImplementedException();
#elif NETSTANDARD2_0
            throw new NotImplementedException();
#else
            throw new NotImplementedException();
            //var httpClient = new HttpClient();

            //httpClient.DefaultRequestHeaders.Authorization =
            //   new AuthenticationHeaderValue("Bearer", _AccessToken);
            //httpClient.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
            //httpClient.DefaultRequestHeaders.Add("OData-Version", "4.0");
            //var batchid = "batch_" + Guid.NewGuid().ToString();

            httpClient.DefaultRequestHeaders.Authorization =
               new AuthenticationHeaderValue("Bearer", _crmWebAPIConfig.AccessToken);
            httpClient.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
            httpClient.DefaultRequestHeaders.Add("OData-Version", "4.0");
            var batchid = "batch_" + Guid.NewGuid().ToString();

            //int contentID = 1;
            //foreach (var data in datalist)
            //{
            //    HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, _apiUrl + entityCollection);

            int contentID = 1;
            foreach (var data in datalist)
            {
                HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, _crmWebAPIConfig.APIUrl + entityCollection);

            //batchContent.Add(changeSetContent);

            //HttpRequestMessage batchRequest = new HttpRequestMessage(HttpMethod.Post, _apiUrl + "$batch");

            HttpRequestMessage batchRequest = new HttpRequestMessage(HttpMethod.Post, _crmWebAPIConfig.APIUrl + "$batch");

            //var batchstring = await batchRequest.Content.ReadAsStringAsync();

            //var response = await httpClient.SendAsync(batchRequest);
            //var responseString = response.Content.ReadAsStringAsync();
            //MultipartMemoryStreamProvider batchStream = await response.Content.ReadAsMultipartAsync(); ;
            //var changesetStream = batchStream.Contents.FirstOrDefault();

            //StreamContent changesetFixedContent = FixupChangeStreamDueToBug(changesetStream);

            //var changesetFixedStream = await changesetFixedContent.ReadAsMultipartAsync();
            //CRMBatchResult finalResult = new CRMBatchResult();
            //finalResult.ResultItems = new List<CRMBatchResultItem>();

            //foreach (var responseContent in changesetFixedStream.Contents)
            //{               
            //    var fixedREsponseContent = FixupToAddCorrectHttpContentType(responseContent);
            //    var individualResponseString = await fixedREsponseContent.ReadAsStringAsync();
            //    var indivdualResponse = await fixedREsponseContent.ReadAsHttpResponseMessageAsync();              
            //    var idString = indivdualResponse.Headers.GetValues("OData-EntityId").FirstOrDefault();
            //    idString = idString.Replace(_apiUrl + entityCollection, "").Replace("(", "").Replace(")", "");
            //    CRMBatchResultItem resultItem = new CRMBatchResultItem();
            //    resultItem.EntityID = Guid.Parse(idString);
            //    finalResult.ResultItems.Add(resultItem);
            //}

            foreach (var responseContent in changesetFixedStream.Contents)
            {               
                var fixedREsponseContent = FixupToAddCorrectHttpContentType(responseContent);
                var individualResponseString = await fixedREsponseContent.ReadAsStringAsync();
                var indivdualResponse = await fixedREsponseContent.ReadAsHttpResponseMessageAsync();              
                var idString = indivdualResponse.Headers.GetValues("OData-EntityId").FirstOrDefault();
                idString = idString.Replace(_crmWebAPIConfig.APIUrl + entityCollection, "").Replace("(", "").Replace(")", "");
                CRMBatchResultItem resultItem = new CRMBatchResultItem();
                resultItem.EntityID = Guid.Parse(idString);
                finalResult.ResultItems.Add(resultItem);
            }

            return finalResult;
#endif
        }
        /// <summary>
        /// currently the content type for individual responses is missing msgtype=response that the API needs to parse it
        /// </summary>
        /// <param name="changesetStream"></param>
        /// <returns></returns>
        private static HttpContent FixupToAddCorrectHttpContentType(HttpContent changesetStream)
        {
            changesetStream.Headers.Remove("Content-Type");
            changesetStream.Headers.Add("Content-Type", "application/http; msgtype=response");

            return changesetStream;

        }

        /// <summary>
        /// currently the change set is missing a new line at the end - this fixes that
        /// </summary>
        /// <param name="changesetStream"></param>
        /// <returns></returns>
        private static StreamContent FixupChangeStreamDueToBug(HttpContent changesetStream)
        {
            Stream changesetResultStream = changesetStream.ReadAsStreamAsync().Result;
            MemoryStream tempStream = new MemoryStream();
            changesetResultStream.CopyTo(tempStream);

            tempStream.Seek(0, SeekOrigin.End);
            StreamWriter writer = new StreamWriter(tempStream);
            //add new line to fix stream
            writer.WriteLine();
            writer.Flush();
            tempStream.Position = 0;

            StreamContent changesetFixedContent = new StreamContent(tempStream);
            foreach (var header in changesetStream.Headers)
            {
                changesetFixedContent.Headers.Add(header.Key, header.Value);
            }

            return changesetFixedContent;
        }


        /// <summary>
        /// Update or Insert based on a match with the entityID
        /// </summary>
        /// <param name="entityCollection"></param>
        /// <param name="entityID"></param>
        /// <param name="data"></param>
        /// <param name="preventCreate"></param>
        /// <param name="preventUpdate"></param>
        /// <returns></returns>
        public async Task<CRMUpdateResult> Update(string entityCollection, Guid entityID, object data, bool Upsert = true)
        {
            return await Update(entityCollection, entityID.ToString(), data, Upsert);
        }
        /// <summary>
        /// Update or insert based on match with key provided in form of Field = Value
        /// </summary>
        /// <param name="entityCollection"></param>
        /// <param name="key"></param>
        /// <param name="data"></param>
        /// <param name="preventCreate"></param>
        /// <param name="preventUpdate"></param>
        /// <returns></returns>
        public async Task<CRMUpdateResult> Update(string entityCollection, string key, object data, bool Upsert = true)
        {
            await CheckAuthToken();
            CRMUpdateResult result = new CRMUpdateResult();
            var fullUrl = _crmWebAPIConfig.APIUrl + entityCollection;

            HttpRequestMessage request = new HttpRequestMessage(new HttpMethod("PATCH"), fullUrl + "(" + key + ")");

            var jsonData = JsonConvert.SerializeObject(data);

            request.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");

            if (!Upsert)
                request.Headers.Add("If-Match", "*");

            var response = await _httpClient.SendAsync(request);

            result.EntityID = GetEntityIDFromResponse(response);


            if (!response.IsSuccessStatusCode)
            {
                if ((response.StatusCode == HttpStatusCode.PreconditionFailed) &&
                        (!Upsert))
                {
                    return result;
                }
                EnsureSuccessStatusCode(response, jsonData: jsonData);
            }

            return result;

        }
        /// <summary>
        /// delete record
        /// </summary>
        /// <param name="entityCollection"></param>
        /// <param name="entityID"></param>
        /// <returns></returns>
        public async Task Delete(string entityCollection, Guid entityID)
        {
            await CheckAuthToken();

            var response = await _httpClient.DeleteAsync(_crmWebAPIConfig.APIUrl + entityCollection + "(" + entityID.ToString() + ")");

            EnsureSuccessStatusCode(response);

        }
        /// <summary>
        /// execute an unbound function
        /// </summary>
        /// <param name="function"></param>
        /// <param name="parameters"></param>
        /// <returns></returns>
        private async Task<ExpandoObject> ExecuteFunction(string function, KeyValuePair<string, object>[] parameters = null)
        {
            await CheckAuthToken();
            var fullUrl = string.Empty;
            fullUrl = BuildFunctionActionURI(function, parameters);
            var results = await _httpClient.GetAsync(fullUrl);
            EnsureSuccessStatusCode(results);
            var data = await results.Content.ReadAsStringAsync();
            var values = JsonConvert.DeserializeObject<ExpandoObject>(data);
            return values;
        }



        /// <summary>
        /// Execute a bound function using object parameters
        /// </summary>
        /// <param name="function"></param>
        /// <param name="entityCollection"></param>
        /// <param name="entityID"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public async Task<ExpandoObject> ExecuteFunction(string function, string entityCollection, Guid entityID, object data)
        {
            List<KeyValuePair<string, object>> list = ConvertObjectToKeyValuePair(data);

            return await ExecuteFunction(function, entityCollection, entityID, list.ToArray());
        }
        /// <summary>
        /// execute an unbound function using object parameters
        /// </summary>
        /// <param name="function"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public async Task<ExpandoObject> ExecuteFunction(string function, object data = null)
        {
            List<KeyValuePair<string, object>> list = ConvertObjectToKeyValuePair(data);

            return await ExecuteFunction(function, list.ToArray());
        }

        /// <summary>
        /// Execute an Action
        /// </summary>
        /// <param name="action"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public async Task<ExpandoObject> ExecuteAction(string action, object data)
        {
            await CheckAuthToken();

            var fullUrl = string.Format("{0}{1}", _crmWebAPIConfig.APIUrl, action);

            HttpRequestMessage request = new HttpRequestMessage(new HttpMethod("Post"), fullUrl);

            var jsonData = JsonConvert.SerializeObject(data);

            request.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");

            var results = await _httpClient.SendAsync(request);

            EnsureSuccessStatusCode(results, jsonData: jsonData);

            var resultData = await results.Content.ReadAsStringAsync();
            var values = JsonConvert.DeserializeObject<ExpandoObject>(resultData);
            return values;
        }
        /// <summary>
        /// Execute a bound action
        /// </summary>
        /// <param name="action"></param>
        /// <param name="entityCollection"></param>
        /// <param name="entityID"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public async Task<ExpandoObject> ExecuteAction(string action, string entityCollection, Guid entityID, object data)
        {
            return await ExecuteAction(string.Format("{0}({1})/{2}", entityCollection, entityID.ToString(), action), data);
        }

        /// <summary>
        /// Helper function to convert object to KVP
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>
        private static List<KeyValuePair<string, object>> ConvertObjectToKeyValuePair(object data)
        {
            if (data != null)
            {
                if (data.GetType() == typeof(ExpandoObject))
                {
                    var dataExpand = data as ExpandoObject;
                    return dataExpand.ToList();
                }
                else
                {
                    Type type = data.GetType();
                    IList<PropertyInfo> props = new List<PropertyInfo>(type.GetProperties());
                    List<KeyValuePair<string, object>> list = new List<KeyValuePair<string, object>>();

                    foreach (PropertyInfo prop in props)
                    {
                        object propValue = prop.GetValue(data, null);

                        list.Add(new KeyValuePair<string, object>(prop.Name, propValue));
                    }

                    return list;
                }
            }

            else return new List<KeyValuePair<string, object>>();
        }
        /// <summary>
        /// Helper function to build the url for functions and actions
        /// </summary>
        /// <param name="function"></param>
        /// <param name="parameters"></param>
        /// <returns></returns>
        private string BuildFunctionActionURI(string function, KeyValuePair<string, object>[] parameters)
        {
            string fullUrl;
            if (parameters != null)
            {
                List<string> paramList = new List<string>();
                List<string> valueList = new List<string>();
                int paramCount = 1;
                foreach (var parm in parameters)
                {
                    if (parm.Value.GetType() == typeof(String))
                    {
                        valueList.Add(string.Format("@p{0}='{1}'", paramCount, parm.Value));
                    }
                    else if (parm.Value.GetType().GetTypeInfo().IsPrimitive)
                    {
                        valueList.Add(string.Format("@p{0}={1}", paramCount, parm.Value));
                    }
                    else
                    {
                        valueList.Add(string.Format("@p{0}={1}", paramCount, JsonConvert.SerializeObject(parm.Value)));
                    }
                    paramList.Add(string.Format("{0}=@p{1}", parm.Key, paramCount));
                    paramCount++;
                }

                fullUrl = string.Format("{0}{1}({2})?{3}", _crmWebAPIConfig.APIUrl, function, string.Join(",", paramList), string.Join("&", valueList));
            }
            else
            {
                fullUrl = string.Format("{0}{1}()", _crmWebAPIConfig.APIUrl, function);
            }

            return fullUrl;
        }

        /// <summary>
        /// helper function to build query url
        /// </summary>
        /// <param name="uri"></param>
        /// <param name="queryOptions"></param>
        /// <returns></returns>
        private string BuildGetUrl(string uri, CRMGetListOptions queryOptions)
        {
            var fullurl = _crmWebAPIConfig.APIUrl + uri;

            if (queryOptions != null)
            {
                bool firstParam = true;

                if (!string.IsNullOrEmpty(queryOptions.TrackChangesLink))
                {
                    fullurl = queryOptions.TrackChangesLink;
                    firstParam = false;
                }

                if (queryOptions.Select != null)
                {

                    if (firstParam)
                        fullurl = String.Format("{0}?$select={1}", fullurl, String.Join(",", queryOptions.Select));
                    else
                        fullurl = String.Format("{0}&$select={1}", fullurl, String.Join(",", queryOptions.Select));
                    firstParam = false;

                }
                if (queryOptions.OrderBy != null)
                {

                    if (firstParam)
                        fullurl = String.Format("{0}?$orderby={1}", fullurl, String.Join(",", queryOptions.OrderBy));
                    else
                        fullurl = String.Format("{0}&$orderby={1}", fullurl, String.Join(",", queryOptions.OrderBy));
                    firstParam = false;

                }
                if (queryOptions.Filter != null)
                {
                    if (firstParam)
                        fullurl = fullurl + "?$filter=" + queryOptions.Filter;
                    else
                        fullurl = fullurl + "&$filter=" + queryOptions.Filter;
                    firstParam = false;
                }
                if (queryOptions.Apply != null)
                {
                    if (firstParam)
                        fullurl = fullurl + "?$apply=" + queryOptions.Apply;
                    else
                        fullurl = fullurl + "&$apply=" + queryOptions.Apply;
                    firstParam = false;
                }
                if (queryOptions.IncludeCount)
                {
                    if (firstParam)
                        fullurl = fullurl + "?$count=true";
                    else
                        fullurl = fullurl + "&$count=true";
                    firstParam = false;
                }

                if (queryOptions.Skip > 0)
                {
                    if (firstParam)
                        fullurl = fullurl + string.Format("?$skip={0}", queryOptions.Skip);
                    else
                        fullurl = fullurl + string.Format("&$skip={0}", queryOptions.Skip);
                    firstParam = false;
                }
                if (queryOptions.Top > 0)
                {
                    if (firstParam)
                        fullurl = fullurl + string.Format("?$top={0}", queryOptions.Top);
                    else
                        fullurl = fullurl + string.Format("&$top={0}", queryOptions.Top);
                    firstParam = false;
                }
                if (queryOptions.Expand != null)
                    BuildExpandQueryURLOptions(queryOptions, ref fullurl, ref firstParam);

                BuildAdvancedQueryURLOptions(queryOptions, ref fullurl, ref firstParam);
            }

            return fullurl;
        }

        private void BuildExpandQueryURLOptions(CRMGetListOptions queryOptions, ref string fullurl, ref bool firstParam)
        {
            List<string> expands = new List<string>();

            foreach (var expand in queryOptions.Expand)
            {
                List<string> expandOptions = new List<string>();

                if (expand.Select != null)
                    expandOptions.Add(String.Format("$select={0}", String.Join(",", expand.Select)));

                if (expand.OrderBy != null)
                    expandOptions.Add(String.Format("$orderby={0}", String.Join(",", expand.OrderBy)));

                if (expand.Filter != null)
                    expandOptions.Add("$filter=" + expand.Filter);

                if (expand.Top > 0)
                    expandOptions.Add(string.Format("$top={0}", expand.Top));

                if (expandOptions.Count > 0)
                    expands.Add(string.Format("{0}({1})", expand.Property, string.Join(";", expandOptions)));
                else
                    expands.Add(string.Format("{0}", expand.Property));

            }
            if (expands.Count > 0)
            {
                if (firstParam)
                    fullurl = fullurl + string.Format("?$expand={0}", String.Join(",", expands));
                else
                    fullurl = fullurl + string.Format("&$expand={0}", String.Join(",", expands));
                firstParam = false;
            }

        }

        private static void BuildAdvancedQueryURLOptions(CRMGetListOptions queryOptions, ref string fullurl, ref bool firstParam)
        {
            if (queryOptions.SystemQuery != Guid.Empty)
            {
                if (firstParam)
                    fullurl = fullurl + string.Format("?savedQuery={0}", queryOptions.SystemQuery.ToString());
                else
                    fullurl = fullurl + string.Format("&savedQuery={0}", queryOptions.SystemQuery.ToString());
                firstParam = false;
            }
            if (queryOptions.UserQuery != Guid.Empty)
            {
                if (firstParam)
                    fullurl = fullurl + string.Format("?userQuery={0}", queryOptions.UserQuery.ToString());
                else
                    fullurl = fullurl + string.Format("&userQuery={0}", queryOptions.UserQuery.ToString());
                firstParam = false;
            }
            if (!string.IsNullOrEmpty(queryOptions.FetchXml))
            {
                if (firstParam)
                    fullurl = fullurl + string.Format("?fetchXml={0}", Uri.EscapeUriString(queryOptions.FetchXml));
                else
                    fullurl = fullurl + string.Format("&fetchXml={0}", Uri.EscapeUriString(queryOptions.FetchXml));
                firstParam = false;
            }
        }

        /// <summary>
        /// helper function to make sure token refresh happens as needed if refresh method provided
        /// </summary>
        private async Task<string> CheckAuthToken()
        {
            if (_crmWebAPIConfig.GetAccessToken == null)
                return _crmWebAPIConfig.AccessToken;
            var newToken = await _crmWebAPIConfig.GetAccessToken(_crmWebAPIConfig.APIUrl);
            if (newToken != _crmWebAPIConfig.AccessToken)
            {
                _httpClient.DefaultRequestHeaders.Remove("Authorization");
                _httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", newToken);
                _crmWebAPIConfig.AccessToken = newToken;
            }
            return _crmWebAPIConfig.AccessToken;
        }
        /// <summary>
        /// helper method to setup the httpclient defaults
        /// </summary>
        /// <param name="callerID"></param>
        private void SetHttpClientDefaults(Guid callerID)
        {
            _httpClient.DefaultRequestHeaders.Add("Accept", "application/json");
            _httpClient.DefaultRequestHeaders.Add("OData-MaxVersion", "4.0");
            _httpClient.DefaultRequestHeaders.Add("OData-Version", "4.0");
            //_httpClient.DefaultRequestHeaders.Add("Prefer", "odata.maxpagesize=1");
            if (callerID != Guid.Empty)
                _httpClient.DefaultRequestHeaders.Add("MSCRMCallerID", callerID.ToString());

            _httpClient.Timeout = new TimeSpan(0, 2, 0);
        }
        /// <summary>
        ///  helper method to setup the request track-changes header
        /// </summary>
        /// <param name="Request"></param>
        /// <param name="QueryOptions"></param>
        private void FillPreferHeader(HttpRequestMessage Request, CRMGetListOptions QueryOptions)
        {
            if (QueryOptions == null)
                return;

            var preferList = new List<string>();

            if (QueryOptions.FormattedValues)
                preferList.Add("odata.include-annotations=\"OData.Community.Display.V1.FormattedValue\"");

            if (QueryOptions.TrackChanges)
                preferList.Add("odata.track-changes");

            //preferList.Add("odata.maxpagesize=1000");

            if (preferList.Count > 0)
                Request.Headers.Add("Prefer", string.Join(",", preferList));
        }

        /// <summary>
        /// Helper method to get ID from response
        /// </summary>
        /// <param name="response"></param>
        /// <returns></returns>
        private static Guid GetEntityIDFromResponse(HttpResponseMessage response)
        {
            if ((response == null) || (response.Headers == null))
                return Guid.Empty;
            if (!response.Headers.Contains("OData-EntityId"))
                return Guid.Empty;

            var idString = response.Headers.GetValues("OData-EntityId").FirstOrDefault();

            if (string.IsNullOrEmpty(idString))
                return Guid.Empty;

            string[] entityIDseps = { "(", ")" };
            string[] entityIDParts = idString.Split(entityIDseps, StringSplitOptions.None);

            if (entityIDParts.Length < 2)
                return Guid.Empty;

            var idGuid = Guid.Empty;

            //if alternate key was used to perform an upsert, guid not currently returned
            //the call returns the alternate key which is not in guid format
            Guid.TryParse(entityIDParts[1], out idGuid);

            return idGuid;
        }
        /// <summary>
        /// Helper method to check the response status and generate a well formatted error
        /// </summary>
        /// <param name="response"></param>
        private static void EnsureSuccessStatusCode(HttpResponseMessage response, string jsonData = null)
        {
            if (response.IsSuccessStatusCode)
                return;

            string message = GetErrorMessageText(response);

            var exception = new Xrm.Tools.WebAPI.Results.CRMWebAPIException(message);

            if (jsonData != null)
                exception.JSON = jsonData;

            throw exception;

        }
        /// <summary>
        /// Helper method to extract error message text
        /// </summary>
        /// <param name="response"></param>
        /// <returns></returns>
        private static string GetErrorMessageText(HttpResponseMessage response)
        {
            if (response?.Content == null)
            {
                return "Error occurred. request response is empty";
            }

            string errorData = response.Content.ReadAsStringAsync().Result;
            string mediaType = response.Content?.Headers?.ContentType?.MediaType;

            if (string.IsNullOrWhiteSpace(errorData) ||
                string.IsNullOrWhiteSpace(mediaType) ||
                mediaType.Equals("text/plain"))
            {
                return errorData;
            }

            if (mediaType.Equals("application/json"))
            {
                JObject jcontent = (JObject)JsonConvert.DeserializeObject(errorData);
                IDictionary<string, JToken> d = jcontent;

                if (d.ContainsKey("error"))
                {
                    JObject error = (JObject)jcontent.Property("error").Value;
                    return (String)error.Property("message")?.Value ?? errorData;
                }

                if (d.ContainsKey("Message"))
                    return (String)jcontent.Property("Message").Value;
            }
            else if (mediaType.Equals("text/html"))
            {
                return "HTML Error Content:\n\n" + errorData;
            }
            else
            {
                return $"Error occurred and no handler is available for content in the {response.Content.Headers.ContentType.MediaType} format.";
            }

            return errorData;
        }


        /// <summary>
        /// Helper method to relace the '_x002e_', '_x0040_' and '_TEXT_value' for the '.', '@' and 'TEXT'
        /// If some property is found with the same name no action will be done.
        /// </summary>
        /// <param name="response"></param>
        private static void FormatResultProperties(JObject obj)
        {
            var properties = obj.Properties().ToList();

            foreach (var property in properties)
            {
                var propName = property.Name;
                if (!propName.Contains("_value") && !propName.Contains("_x002e_") &&
                    !propName.Contains("_x0040_"))
                    continue;

                var matchValue = new Regex("^(_)(.+)(_value)\\b").Match(propName);

                if (matchValue.Success)
                    propName = matchValue.Groups[2].Value;

                propName = propName.Replace("_x002e_", ".").Replace("_x0040_", "@");

                if (obj[propName] == null)
                    obj[propName] = property.Value;
            }
        }
    }
}
