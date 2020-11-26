using Azure;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Storage.Blobs;
using Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.CognitiveServices.Knowledge.QnAMaker;
using Microsoft.Azure.CognitiveServices.Knowledge.QnAMaker.Models;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;


namespace AzureCognitiveSearch.QnAIntegrationCustomSkill
{
    public static class InitializeAccelerator
    {
        // initializes the accelerator solution. 
        [FunctionName("init-accelerator")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req,
            ILogger log, ExecutionContext executionContext)
        {
            var storageConnectionString = GetAppSetting("AzureWebJobsStorage");
            var prefix = GetAppSetting("prefix");
            var searchServiceEndpoint = $"https://{GetAppSetting("SearchServiceName")}.search.windows.net/";
            var basePath = Path.Join(executionContext.FunctionAppDirectory, "Assets");
            string responseMessage;

            try
            {
                await CreateContainer(storageConnectionString, prefix, log);
                await CreateDataSource(storageConnectionString, searchServiceEndpoint, basePath, prefix, log);
                await CreateIndex(searchServiceEndpoint, prefix, log);
                await CreateSkillSet(searchServiceEndpoint, basePath, prefix, log);
                await CreateIndexer(searchServiceEndpoint, basePath, prefix, log);

                responseMessage = "Initialized accelerator successfully.";
            }
            catch (Exception e)
            {
                responseMessage = "Failed to initialize accelerator " + e.Message;
            }

            return new OkObjectResult(responseMessage);
        }

        // returns dynamic ARM template with kbid to update configuration of the function app. 
        [FunctionName("init-kb")]
        public static async Task<IActionResult> initKB(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req,
            ILogger log, ExecutionContext executionContext)
        {
            var path = Path.Join(executionContext.FunctionAppDirectory, "Assets", "AppSettingsUpdate.json");
            var kbId = await CreateKB(log);
            string responseARM;
            using (StreamReader r = new StreamReader(path))
            {
                var body = r.ReadToEnd();
                responseARM = body.Replace("{{kbId}}", kbId);
            }
            return new OkObjectResult(responseARM);
        }

        private static async Task<string> CreateKB(ILogger log)
        {
            var qnaClient = new QnAMakerClient(new ApiKeyServiceClientCredentials(GetAppSetting("QnAAuthoringKey")))
            {
                Endpoint = $"https://{GetAppSetting("QnAServiceName")}.cognitiveservices.azure.com"
            };

            var createKbDTO = new CreateKbDTO { Name = "search", Language = "English" };
            var operation = await qnaClient.Knowledgebase.CreateAsync(createKbDTO);
            operation = await MonitorOperation(qnaClient, operation, log);
            var kbId = operation.ResourceLocation.Replace("/knowledgebases/", string.Empty);
            log.LogInformation("init-kb: Created KB " + kbId);
            return kbId;
        }

        private static async Task CreateContainer(string connectionString, string prefix, ILogger log)
        {
            string containerName = string.Concat(prefix, Constants.containerName);
            try
            {
                log.LogInformation("init-accelerator: Creating container " + containerName);

                BlobServiceClient blobServiceClient = new BlobServiceClient(connectionString);
                BlobContainerClient containerClient = await blobServiceClient.CreateBlobContainerAsync(containerName);
            }
            catch (Exception e)
            {
                log.LogError("init-accelerator: container creation failed " + e.Message);
                throw new Exception(e.Message);
            }
        }

        private static async Task CreateDataSource(string storageConnection, string searchServiceEndpoint, string basePath, string prefix, ILogger log)
        {
            string dataSourceName = string.Concat(prefix, Constants.dataSourceName);
            log.LogInformation("init-accelerator: Creating data source " + dataSourceName);

            try
            {
                string uri = string.Format("{0}/datasources/{1}?api-version={2}", searchServiceEndpoint, dataSourceName, Constants.apiVersion);
                var path = Path.Combine(basePath, "DataSource.json");
                using (StreamReader r = new StreamReader(path))
                {
                    var body = r.ReadToEnd();
                    body = body.Replace("{{datasourcename}}", dataSourceName);
                    body = body.Replace("{{connectionString}}", storageConnection);
                    body = body.Replace("{{containerName}}", string.Concat(prefix, Constants.containerName));

                    var response = await Put(uri, body);
                    if (response.StatusCode != HttpStatusCode.Created)
                    {
                        var responseBody = await response.Content.ReadAsStringAsync();
                        log.LogError("init-accelerator: error while creating data source " + responseBody);
                        throw new Exception(responseBody);
                    }

                }
            }
            catch (Exception e)
            {
                log.LogError("init-accelerator: error while creating data source " + e.Message);
                throw new Exception(e.Message);
            }
        }

        private static async Task CreateIndex(string searchServiceEndpoint, string prefix, ILogger log)
        {
            string indexName = string.Concat(prefix, Constants.indexName);
            log.LogInformation("init-accelerator: Creating index " + indexName);
            try
            {
                var idxclient = new SearchIndexClient(new Uri(searchServiceEndpoint), new AzureKeyCredential(GetAppSetting("SearchServiceApiKey")));
                SearchIndex index = new SearchIndex(indexName)
                {
                    Fields =
                {
                    new SearchField("content", SearchFieldDataType.String) { IsSearchable = true, IsSortable = false, IsFilterable = false, IsFacetable = false},
                    new SearchField("metadata_storage_path", SearchFieldDataType.String) { IsSearchable = true, IsSortable = false, IsFilterable = false, IsFacetable = false },
                    new SearchField("id", SearchFieldDataType.String) { IsKey = true, IsSearchable = true, IsSortable = false, IsFilterable = false, IsFacetable = false },
                    new SearchField("metadata_storage_name", SearchFieldDataType.String) { IsSearchable = true, IsSortable = false, IsFilterable = false, IsFacetable = false },
                    new SearchField("status", SearchFieldDataType.String) { IsSearchable = false, IsSortable = false, IsFilterable = false, IsFacetable = false }
                }
                };

                await idxclient.CreateIndexAsync(index);
            }
            catch (Exception e)
            {
                log.LogError("init-accelerator: Error while creating index " + e.Message);
                throw new Exception(e.Message);
            }
        }

        private static async Task CreateSkillSet(string searchServiceEndpoint, string basePath, string prefix, ILogger log)
        {
            string skillSetName = string.Concat(prefix, Constants.skillSetName);
            log.LogInformation("init-accelerator: Creating Skill Set " + skillSetName);
            try
            {
                string uri = string.Format("{0}/skillsets/{1}?api-version={2}", searchServiceEndpoint, skillSetName, Constants.apiVersion);
                var path = Path.Combine(basePath, "SkillSet.json");
                using (StreamReader r = new StreamReader(path))
                {
                    var body = r.ReadToEnd();
                    body = body.Replace("{{skillset-name}}", skillSetName);
                    body = body.Replace("{{function-name}}", GetAppSetting("WEBSITE_CONTENTSHARE"));
                    body = body.Replace("{{function-code}}", GetAppSetting("FunctionCode"));
                    body = body.Replace("{{cog-svc-allinone-key}}", GetAppSetting("CogServicesKey"));

                    var response = await Put(uri, body);
                    if (response.StatusCode != HttpStatusCode.Created)
                    {
                        var responseBody = await response.Content.ReadAsStringAsync();
                        log.LogError("init-accelerator: Error while creating skill set " + responseBody);
                        throw new Exception(responseBody);
                    }

                }
            }
            catch (Exception e)
            {
                log.LogError("init-accelerator: Error while creating skill set " + e.Message);
                throw new Exception(e.Message);
            }

        }

        private static async Task CreateIndexer(string searchServiceEndpoint, string basePath, string prefix, ILogger log)
        {
            string indexerName = string.Concat(prefix, Constants.indexerName);
            log.LogInformation("init-accelerator: Creating indexer " + indexerName);
            try
            {
                string uri = string.Format("{0}/indexers/{1}?api-version={2}", searchServiceEndpoint, indexerName, Constants.apiVersion);
                var path = Path.Combine(basePath, "Indexer.json");
                using (StreamReader r = new StreamReader(path))
                {
                    var body = r.ReadToEnd();
                    body = body.Replace("{{indexer-name}}", indexerName);
                    body = body.Replace("{{index-name}}", string.Concat(prefix, Constants.indexName));
                    body = body.Replace("{{datasource-name}}", string.Concat(prefix, Constants.dataSourceName));
                    body = body.Replace("{{skillset-name}}", string.Concat(prefix, Constants.skillSetName));

                    var response = await Put(uri, body);
                    if (response.StatusCode != HttpStatusCode.Created)
                    {
                        var responseBody = await response.Content.ReadAsStringAsync();
                        log.LogError("init-accelerator: Error while creating indexer " + responseBody);
                        throw new Exception(responseBody);
                    }

                }
            }
            catch (Exception e)
            {
                log.LogError("init-accelerator: Error while creating indexer " + e.Message);
                throw new Exception(e.Message);
            }

        }

        private static async Task<Operation> MonitorOperation(IQnAMakerClient qnaClient, Operation operation, ILogger log)
        {
            // Loop while operation is running
            for (int i = 0;
                i < 100 && (operation.OperationState == OperationStateType.NotStarted || operation.OperationState == OperationStateType.Running);
                i++)
            {
                log.LogInformation($"Waiting for operation: {operation.OperationId} to complete.");
                await Task.Delay(5000);
                operation = await qnaClient.Operations.GetDetailsAsync(operation.OperationId);
            }

            if (operation.OperationState != OperationStateType.Succeeded)
            {
                log.LogError($"Operation {operation.OperationId} failed to completed. ErrorMessage: {operation.ErrorResponse.Error.Message}");
            }
            return operation;
        }

        private static async Task<HttpResponseMessage> Put(string uri, string body)
        {
            var key = GetAppSetting("SearchServiceApiKey");
            using (var client = new HttpClient())
            using (var request = new HttpRequestMessage())
            {
                request.Method = HttpMethod.Put;
                request.RequestUri = new Uri(uri);

                if (!string.IsNullOrEmpty(body))
                {
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                }

                request.Headers.Add("api-key", $"{key}");
                request.Headers.Add("content", "application/json");

                var response = await client.SendAsync(request);
                return response;
            }
        }

        private static string GetAppSetting(string key)
        {
            return Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Process);
        }
    }
}
