using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using MLS.Agent.JsonContracts;
using Pocket;
using Recipes;

namespace MLS.Agent.Tests
{
    public class AgentService : IDisposable
    {
        private readonly CompositeDisposable disposables = new CompositeDisposable();

        private readonly HttpClient client;

        public AgentService()
        {
           //JsonContratcs.Setup();
            var testServer = CreateTestServer();

            client = testServer.CreateClient();

            disposables.Add(testServer);
            disposables.Add(client);
        }

        public void Dispose() => disposables.Dispose();

        private TestServer CreateTestServer() => new TestServer(CreateWebHostBuilder());

        private IWebHostBuilder CreateWebHostBuilder()
        {
            var builder = new WebHostBuilder()
                          .UseTestEnvironment()
                          .UseStartup<Startup>();

            return builder;
        }

        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request) =>
            client.SendAsync(request);
    }
}
