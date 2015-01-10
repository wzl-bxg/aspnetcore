﻿using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using Microsoft.AspNet.Testing.xunit;
using Microsoft.Framework.Logging;
using Xunit;

namespace E2ETests
{
    public partial class SmokeTests
    {
        [ConditionalTheory]
        [FrameworkSkipCondition(RuntimeFrameworks.Mono)]
        [InlineData(ServerType.IISExpress, KreFlavor.DesktopClr, KreArchitecture.x86, "http://localhost:5001/")]
        public void Publish_And_Run_Tests_On_X86(ServerType serverType, KreFlavor kreFlavor, KreArchitecture architecture, string applicationBaseUrl)
        {
            Publish_And_Run_Tests(serverType, kreFlavor, architecture, applicationBaseUrl);
        }

        [ConditionalTheory]
        [FrameworkSkipCondition(RuntimeFrameworks.DotNet)]
        [InlineData(ServerType.Kestrel, KreFlavor.Mono, KreArchitecture.x86, "http://localhost:5004/")]
        public void Publish_And_Run_Tests_On_Mono(ServerType serverType, KreFlavor kreFlavor, KreArchitecture architecture, string applicationBaseUrl)
        {
            Publish_And_Run_Tests(serverType, kreFlavor, architecture, applicationBaseUrl);
        }

        [ConditionalTheory]
        [FrameworkSkipCondition(RuntimeFrameworks.Mono)]
        [InlineData(ServerType.WebListener, KreFlavor.DesktopClr, KreArchitecture.amd64, "http://localhost:5002/")]
        //https://github.com/aspnet/KRuntime/issues/642
        //[InlineData(ServerType.Helios, KreFlavor.CoreClr, KreArchitecture.amd64, "http://localhost:5001/")]
        public void Publish_And_Run_Tests_On_AMD64(ServerType serverType, KreFlavor kreFlavor, KreArchitecture architecture, string applicationBaseUrl)
        {
            Publish_And_Run_Tests(serverType, kreFlavor, architecture, applicationBaseUrl);
        }

        private void Publish_And_Run_Tests(ServerType serverType, KreFlavor kreFlavor, KreArchitecture architecture, string applicationBaseUrl)
        {
            using (_logger.BeginScope("Publish_And_Run_Tests"))
            {
                _logger.WriteInformation("Variation Details : HostType = {0}, KreFlavor = {1}, Architecture = {2}, applicationBaseUrl = {3}",
                    serverType.ToString(), kreFlavor.ToString(), architecture.ToString(), applicationBaseUrl);

                _startParameters = new StartParameters
                {
                    ServerType = serverType,
                    KreFlavor = kreFlavor,
                    KreArchitecture = architecture,
                    PackApplicationBeforeStart = true
                };

                var testStartTime = DateTime.Now;
                var musicStoreDbName = Guid.NewGuid().ToString().Replace("-", string.Empty);

                _logger.WriteInformation("Pointing MusicStore DB to '{0}'", string.Format(CONNECTION_STRING_FORMAT, musicStoreDbName));

                //Override the connection strings using environment based configuration
                Environment.SetEnvironmentVariable("SQLAZURECONNSTR_DefaultConnection", string.Format(CONNECTION_STRING_FORMAT, musicStoreDbName));

                _applicationBaseUrl = applicationBaseUrl;
                Process hostProcess = null;
                bool testSuccessful = false;

                try
                {
                    hostProcess = DeploymentUtility.StartApplication(_startParameters, musicStoreDbName, _logger);

                    _httpClientHandler = new HttpClientHandler() { UseDefaultCredentials = true };
                    _httpClient = new HttpClient(_httpClientHandler) { BaseAddress = new Uri(applicationBaseUrl) };

                    HttpResponseMessage response = null;
                    string responseContent = null;
                    var initializationCompleteTime = DateTime.MinValue;

                    //Request to base address and check if various parts of the body are rendered & measure the cold startup time.
                    //Add retry logic since tests are flaky on mono due to connection issues
                    for (int retryCount = 0; retryCount < 3; retryCount++)
                    {
                        try
                        {
                            response = _httpClient.GetAsync(string.Empty).Result;
                            responseContent = response.Content.ReadAsStringAsync().Result;
                            initializationCompleteTime = DateTime.Now;
                            _logger.WriteInformation("[Time]: Approximate time taken for application initialization : '{0}' seconds",
                                (initializationCompleteTime - testStartTime).TotalSeconds.ToString());
                            break; //Went through successfully
                        }
                        catch (AggregateException exception)
                        {
                            // Both type exceptions thrown by Mono which are resolved by retry logic
                            if (exception.InnerException is HttpRequestException || exception.InnerException is WebException)
                            {
                                _logger.WriteWarning("Failed to complete the request.", exception);
                                _logger.WriteWarning("Retrying request..");
                                Thread.Sleep(1 * 1000); //Wait for a second before retry
                            }
                        }
                    }

                    VerifyHomePage(response, responseContent, true);

                    //Static files are served?
                    VerifyStaticContentServed();

                    if (serverType != ServerType.IISExpress)
                    {
                        if (Directory.GetFiles(_startParameters.ApplicationPath, "*.cmd", SearchOption.TopDirectoryOnly).Length > 0)
                        {
                            throw new Exception("packExclude parameter values are not honored.");
                        }
                    }

                    var testCompletionTime = DateTime.Now;
                    _logger.WriteInformation("[Time]: All tests completed in '{0}' seconds.", (testCompletionTime - initializationCompleteTime).TotalSeconds.ToString());
                    _logger.WriteInformation("[Time]: Total time taken for this test variation '{0}' seconds.", (testCompletionTime - testStartTime).TotalSeconds.ToString());
                    testSuccessful = true;
                }
                finally
                {
                    if (!testSuccessful)
                    {
                        _logger.WriteError("Some tests failed. Proceeding with cleanup.");
                    }

                    DeploymentUtility.CleanUpApplication(_startParameters, hostProcess, musicStoreDbName, _logger);
                }
            }
        }
    }
}