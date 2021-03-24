using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using McMaster.Extensions.CommandLineUtils;
using Newtonsoft.Json;
using RazorLight;

namespace dotnet_gqlgen
{
    public class Program
    {
        [Argument(0, Description = "Path to the GraphQL schema file or a GraphQL introspection endpoint")]
        [Required]
        public string Source { get; }

        [Option(LongName = "header", ShortName = "h", Description = "Headers to pass to GraphQL introspection endpoint. Use \"Authorization=Bearer eyJraWQ,X-API-Key=abc,...\"")]
        public string HeaderValues { get; }

        [Option(LongName = "namespace", ShortName = "n", Description = "Namespace to generate code under")]
        public string Namespace { get; } = "Generated";

        [Option(LongName = "client_class_name", ShortName = "c", Description = "Name for the client class")]
        public string ClientClassName { get; } = "GraphQLClient";
        [Option(LongName = "scalar_mapping", ShortName = "m", Description = "Map of custom schema scalar types to dotnet types. Use \"GqlType=DotNetClassName,ID=Guid,...\"")]
        public string ScalarMapping { get; }
        [Option(LongName = "output", ShortName = "o", Description = "Output directory")]
        public string OutputDir { get; } = "output";

        public Dictionary<string, string> dotnetToGqlTypeMappings = new Dictionary<string, string> {
            {"string", "String"},
            {"String", "String"},
            {"int", "Int!"},
            {"Int32", "Int!"},
            {"double", "Float!"},
            {"bool", "Boolean!"},
        };

        public static Task<int> Main(string[] args) => CommandLineApplication.ExecuteAsync<Program>(args);

        private async void OnExecute()
        {
            try
            {
                Uri uriResult;
                bool isGraphQlEndpoint = Uri.TryCreate(Source, UriKind.Absolute, out uriResult)
                                && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);

                string schemaText = null;
                bool isIntroSpectionFile = false;

                if (isGraphQlEndpoint)
                {
                    Console.WriteLine($"Loading from {Source}...");
                    using (var httpClient = new HttpClient())
                    {
                        foreach (var header in SplitMultiValueArgument(HeaderValues))
                        {
                            httpClient.DefaultRequestHeaders.Add(header.Key, header.Value);
                        }

                        Dictionary<string, string> request = new Dictionary<string, string>();
                        request["query"] = IntroSpectionQuery.Query;
                        request["operationName"] = "IntrospectionQuery";

                        var response = httpClient
                            .PostAsync(Source, 
                            new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json")).GetAwaiter().GetResult();

                        schemaText = await response.Content.ReadAsStringAsync();
                        isIntroSpectionFile = true;
                    }
                }
                else
                {
                    Console.WriteLine($"Loading {Source}...");
                    schemaText = File.ReadAllText(Source);
                    isIntroSpectionFile = Path.GetExtension(Source).Equals(".json", StringComparison.OrdinalIgnoreCase);
                }                

                var mappings = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(ScalarMapping))
                {
                    SplitMultiValueArgument(ScalarMapping).ToList().ForEach(i => {
                        dotnetToGqlTypeMappings[i.Value] = i.Key;
                        mappings[i.Key] = i.Value;
                    });
                }

                // parse into AST
                var typeInfo = !isIntroSpectionFile ?
                    SchemaCompiler.Compile(schemaText, mappings) :
                    IntrospectionCompiler.Compile(schemaText, mappings);

                Console.WriteLine($"Generating types in namespace {Namespace}, outputting to {ClientClassName}.cs");

                // pass the schema to the template
                var engine = new RazorLightEngineBuilder()
                    .UseEmbeddedResourcesProject(typeof(Program))
                    .UseMemoryCachingProvider()
                    .Build();

                var allTypes = typeInfo.Types.Concat(typeInfo.Inputs).ToDictionary(k => k.Key, v => v.Value);

                string result = await engine.CompileRenderAsync("types.cshtml", new
                {
                    Namespace = Namespace,
                    SchemaFile = Source,
                    Types = allTypes,
                    Enums = typeInfo.Enums,
                    Mutation = typeInfo.Mutation,
                    CmdArgs = $"-n {Namespace} -c {ClientClassName} -m {ScalarMapping}"
                });
                Directory.CreateDirectory(OutputDir);
                File.WriteAllText($"{OutputDir}/GeneratedTypes.cs", result);

                result = await engine.CompileRenderAsync("client.cshtml", new
                {
                    Namespace = Namespace,
                    SchemaFile = Source,
                    Query = typeInfo.Query,
                    Mutation = typeInfo.Mutation,
                    ClientClassName = ClientClassName,
                    Mappings = dotnetToGqlTypeMappings
                });
                File.WriteAllText($"{OutputDir}/{ClientClassName}.cs", result);

                foreach(var mutation in typeInfo.Mutation.Fields)
                {
                    // result = await engine.CompileRenderAsync("blazor.cshtml", new
                    // {
                    //     Namespace = Namespace,
                    //     Mutation = mutation,
                    // });

                    var blazorComponent = @$"
@using {Namespace}
@using System.Linq.Expressions
@typeparam TMutationResponse
@inject GraphQLClient GraphQLClient

@if(_state == State.BeforeSubmit) {{
    @BeforeSubmit(_contextBeforeSubmit);
}} else if (_state == State.DuringSubmit) {{
    @DuringSubmit(_contextDuringSubmit);
}} else {{
    @AfterSubmit(_contextAfterSubmit);
}}

@code {{
    [Parameter]
    public RenderFragment<Before{mutation.DotNetName}> BeforeSubmit {{ get; set; }}

    [Parameter]
    public RenderFragment<During{mutation.DotNetName}> DuringSubmit {{ get; set; }}

    [Parameter]
    public RenderFragment<After{mutation.DotNetName}> AfterSubmit {{ get; set; }}

    [Parameter]
    public Expression<Func<{mutation.DotNetType}, TMutationResponse>> Selection {{ get; set; }}

    private State _state = State.BeforeSubmit;

    private Before{mutation.DotNetName} _contextBeforeSubmit;
    private During{mutation.DotNetName} _contextDuringSubmit;
    private After{mutation.DotNetName} _contextAfterSubmit;

    protected override async Task OnInitializedAsync() {{
        _contextBeforeSubmit = new Before{mutation.DotNetName}() {{
            { mutation.Args.Select(arg => $"{arg.DotNetName} = new {arg.DotNetType}(),").Join("\n            ") }
            MutateAsync = MutateAsync,
        }};
        await base.OnInitializedAsync();
    }}

    private async Task MutateAsync() {{
        _contextDuringSubmit = new During{mutation.DotNetName}() {{
            { mutation.Args.Select(arg => $"{arg.DotNetName} = _contextBeforeSubmit.{arg.DotNetName},").Join("\n            ") }
        }};
        var response = await GraphQLClient.MutateAsync(m => m.{mutation.DotNetName}({ mutation.Args.Select(arg => $"_contextBeforeSubmit.{arg.DotNetName}, ").Join() }Selection));
        _contextAfterSubmit = new After{mutation.DotNetName}() {{
            { mutation.Args.Select(arg => $"{arg.DotNetName} = _contextDuringSubmit.{arg.DotNetName},").Join("\n            ") }
            Response = response.Data,
        }};
    }}

    private enum State {{
        BeforeSubmit,
        DuringSubmit,
        AfterSubmit
    }}

    public class Before{mutation.DotNetName} {{
        { mutation.Args.Select(arg => $"public {arg.DotNetType} {arg.DotNetName} {{ get; set; }}").Join("\n            ") }
        public Func<Task> MutateAsync {{ get; init; }}
    }}

    public class During{mutation.DotNetName} {{
        { mutation.Args.Select(arg => $"public {arg.DotNetType} {arg.DotNetName} {{ get; init; }}").Join("\n            ") }
    }}

    public class After{mutation.DotNetName} {{
        { mutation.Args.Select(arg => $"public {arg.DotNetType} {arg.DotNetName} {{ get; init; }}").Join("\n            ") }
        public TMutationResponse Response {{ get; init; }}
    }}
}}
";
                    
                    File.WriteAllText($"{OutputDir}/{mutation.DotNetName}Mutation.razor", blazorComponent);
                }
                
                Console.WriteLine($"Done.");
            }
            catch (Exception e)
            {
                Console.WriteLine("Error: " + e.ToString());
            }
        }

        /// <summary>
        /// Splits an argument value like "value1=v1,value2=v2" into a dictionary.
        /// </summary>
        /// <remarks>Very simple splitter. Eg can't handle comma's or equal signs in values</remarks>
        private Dictionary<string, string> SplitMultiValueArgument(string arg)
        {
            if (string.IsNullOrEmpty(arg))
            {
                return new Dictionary<string, string>();
            }

            return arg
                .Split(',')
                .Select(h => h.Split('='))
                .Where(hs => hs.Length >= 2)
                .ToDictionary(key => key[0], value => value[1]);
        }
    }
}
