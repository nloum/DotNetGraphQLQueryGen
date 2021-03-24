using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Humanizer;
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

@if(_stage == Stage.BeforeMutate) {{
    if (BeforeMutate != null && StateBeforeMutate != null) {{
        @BeforeMutate(StateBeforeMutate);
    }}
}} else if (_stage == Stage.DuringMutate) {{
    if (DuringMutate != null && StateDuringMutate != null) {{
        @DuringMutate(StateDuringMutate);
    }}
}} else {{
    if (AfterMutate != null && StateAfterMutate != null) {{
        @AfterMutate(StateAfterMutate);
    }}
}}

@code {{
    [Parameter]
    public RenderFragment<Before{mutation.DotNetName}> BeforeMutate {{ get; set; }}

    [Parameter]
    public RenderFragment<During{mutation.DotNetName}> DuringMutate {{ get; set; }}

    [Parameter]
    public RenderFragment<After{mutation.DotNetName}> AfterMutate {{ get; set; }}

    [Parameter]
    public Expression<Func<{mutation.DotNetType}, TMutationResponse>> DesiredResults {{ get; set; }}

    private Stage _stage = Stage.BeforeMutate;

    private Before{mutation.DotNetName} _stateBeforeMutate;
        
    public override async Task SetParametersAsync(ParameterView parameters)
    {{
        _stateBeforeMutate = new Before{mutation.DotNetName}() {{
            MutateAsync = MutateAsync
        }};
        await base.SetParametersAsync(parameters);
        { mutation.Args.Select(arg => $"_stateBeforeMutate.{arg.DotNetName} = new {arg.DotNetType}();").Join("\n        ") }
    }}

    [Parameter]
    public Before{mutation.DotNetName} StateBeforeMutate {{
        get => _stateBeforeMutate;
        set {{
            _stateBeforeMutate = value;
            StateBeforeMutateChanged.InvokeAsync(value);
        }}
    }}

    [Parameter]
    public EventCallback<Before{mutation.DotNetName}> StateBeforeMutateChanged {{ get; set; }}

    private During{mutation.DotNetName} _stateDuringMutate;

    [Parameter]
    public During{mutation.DotNetName} StateDuringMutate {{
        get => _stateDuringMutate;
        set {{
            _stateDuringMutate = value;
            StateDuringMutateChanged.InvokeAsync(value);
        }}
    }}

    [Parameter]
    public EventCallback<During{mutation.DotNetName}> StateDuringMutateChanged {{ get; set; }}

    private After{mutation.DotNetName} _stateAfterMutate;

    [Parameter]
    public After{mutation.DotNetName} StateAfterMutate {{
        get => _stateAfterMutate;
        set {{
            _stateAfterMutate = value;
            StateAfterMutateChanged.InvokeAsync(value);
        }}
    }}

    [Parameter]
    public EventCallback<After{mutation.DotNetName}> StateAfterMutateChanged {{ get; set; }}

{
    mutation.Args.Select(arg => @$"
    private {arg.DotNetType} _{arg.DotNetName.Camelize()};

    [Parameter]
    public {arg.DotNetType} {arg.DotNetName} {{
        get => _{arg.DotNetName.Camelize()};
        set {{
            _{arg.DotNetName.Camelize()} = value;
            StateBeforeMutate.{arg.DotNetName} = value;
            {arg.DotNetName}Changed.InvokeAsync(value);
        }}
    }}

    [Parameter]
    public EventCallback<{arg.DotNetType}> {arg.DotNetName}Changed {{ get; set; }}
").Join("\n")
}

    protected override async Task OnInitializedAsync() {{
        await base.OnInitializedAsync();
    }}

    private async Task MutateAsync() {{
        StateDuringMutate = new During{mutation.DotNetName}() {{
            { mutation.Args.Select(arg => $"{arg.DotNetName} = StateBeforeMutate.{arg.DotNetName},").Join("\n            ") }
        }};
        _stage = Stage.DuringMutate;
        var response = await GraphQLClient.MutateAsync(m => m.{mutation.DotNetName}({ mutation.Args.Select(arg => $"StateDuringMutate.{arg.DotNetName}, ").Join() }DesiredResults));
        StateAfterMutate = new After{mutation.DotNetName}() {{
            { mutation.Args.Select(arg => $"{arg.DotNetName} = StateDuringMutate.{arg.DotNetName},").Join("\n            ") }
            Response = response.Data,
        }};
        _stage = Stage.AfterMutate;
    }}

    private enum Stage {{
        BeforeMutate,
        DuringMutate,
        AfterMutate
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
