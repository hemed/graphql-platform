using System.Linq;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using HotChocolate.Execution;
using HotChocolate.Resolvers;

namespace HotChocolate.Adapters.OpenApi;

public class HttpEndpointStatusCodeTests : OpenApiTestBase
{
    protected override void ConfigureStorage(
        IServiceCollection services,
        IOpenApiDefinitionStorage storage,
        OpenApiDiagnosticEventListener? eventListener)
    {
        services.AddGraphQLServer()
            .AddOpenApi()
            .AddOpenApiDefinitionStorage(storage)
            .AddQueryType<StatusCodeQuery>()
            // A request middleware that maps a "NOT_FOUND" GraphQL error onto an
            // explicit HTTP status code, the same way HotChocolate's built-in
            // middlewares (validation, persisted operations, cost) set the status.
            .UseRequest(next => async context =>
            {
                await next(context);

                if (context.Result is OperationResult result
                    && result.Errors.Any(error => error.Code == "NOT_FOUND"))
                {
                    result.ContextData = result.ContextData.Add(
                        ExecutionContextData.HttpStatusCode,
                        HttpStatusCode.NotFound);
                }
            })
            .UseDefaultPipeline();
    }

    [Fact]
    public async Task Http_Honors_Explicit_HttpStatusCode_Override_From_Result_Context()
    {
        // arrange
        // notFoundThing reports a NOT_FOUND error but returns null (a nullable field),
        // so the result has data AND errors, which is the path that previously always
        // produced a 500.
        var storage = new TestOpenApiDefinitionStorage(
            """
            query GetNotFound @http(method: GET, route: "/not-found") {
              notFoundThing
            }
            """);
        var server = CreateTestServer(storage);
        var client = server.CreateClient();

        // act
        var response = await client.GetAsync("/not-found", TestContext.Current.CancellationToken);

        // assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Http_Error_Without_StatusCode_Override_Returns_500()
    {
        // arrange
        // failingThing reports an error that is not mapped to a status code, so the
        // adapter falls back to a generic 500.
        var storage = new TestOpenApiDefinitionStorage(
            """
            query GetFailing @http(method: GET, route: "/failing") {
              failingThing
            }
            """);
        var server = CreateTestServer(storage);
        var client = server.CreateClient();

        // act
        var response = await client.GetAsync("/failing", TestContext.Current.CancellationToken);

        // assert
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    public class StatusCodeQuery
    {
        public string? NotFoundThing(IResolverContext context)
        {
            context.ReportError(
                ErrorBuilder.New()
                    .SetMessage("The thing was not found.")
                    .SetCode("NOT_FOUND")
                    .Build());

            return null;
        }

        public string? FailingThing(IResolverContext context)
        {
            context.ReportError(
                ErrorBuilder.New()
                    .SetMessage("Something went wrong.")
                    .SetCode("INTERNAL")
                    .Build());

            return null;
        }
    }
}
