using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Dynamic;
using System.IO;
using System.Threading.Tasks;
using NJsonSchema.CodeGeneration.TypeScript;
using NSwag;
using NSwag.CodeGeneration.CSharp;
using NSwag.CodeGeneration.TypeScript;

namespace OpenAPIClientGenerator
{
  public class Program
  {
    private enum SupportedLang
    {
      Ts,
      Cs
    }

    private static RootCommand GetRootCommand()
    {
      var rootCommand = new RootCommand
      {
        new Option<string>("--url", "the URL of the swagger.json file for which to generate a client")
        {
          Argument = new Argument<string>
          {
            Arity = ArgumentArity.ExactlyOne
          }
        },
        new Option<DirectoryInfo>("--dir", "The path where the generated client file should be placed")
        {
          Argument = new Argument<DirectoryInfo>
          {
            Arity = ArgumentArity.ExactlyOne
          }
        },
        new Option<SupportedLang>(new[] {"--output-type", "--type", "--ty"}, "Output Language")
        {
          Argument = new Argument<SupportedLang>
          {
            Arity = ArgumentArity.ExactlyOne
          }.FromAmong("Cs", "Ts")
        }
      };

      var optionalArg =
        new Argument<string>
        {
          Arity = ArgumentArity.ZeroOrOne
        };
      optionalArg.SetDefaultValue("MyApp");
      var optionalOpts =
        new Option<string>("--app-name", "The name of the app you want to create client against")
        {
          Argument = optionalArg
        };

      rootCommand.AddOption(optionalOpts);
      return rootCommand;
    }

    private static async Task GenerateClient(OpenApiDocument docs, string generatePath, Func<OpenApiDocument, string> generateCode)
    {
      Console.WriteLine($"Generating {generateCode}");
      var code = generateCode(docs);
      await File.WriteAllTextAsync(generatePath, code);
    }

    private static async Task GenerateTypescriptClient(string url, DirectoryInfo generatePath, string appName) =>
      await GenerateClient(
        await OpenApiDocument.FromUrlAsync(url),
        generatePath.FullName,
        document =>
        {
          var settings = new TypeScriptClientGeneratorSettings();

          settings.TypeScriptGeneratorSettings.TypeStyle = TypeScriptTypeStyle.Interface;
          settings.TypeScriptGeneratorSettings.TypeScriptVersion = 3.5M;
          settings.TypeScriptGeneratorSettings.DateTimeType = TypeScriptDateTimeType.String;

          var generator = new TypeScriptClientGenerator(document, settings);
          var code = generator.GenerateFile();

          return code;
        }
      );

    private static async Task GenerateCSharpClient(string url, DirectoryInfo generatePath, string appName) =>
      await GenerateClient(
        await OpenApiDocument.FromUrlAsync(url),
        generatePath.FullName,
        generateCode: document =>
        {
          var settings = new CSharpClientGeneratorSettings
          {
            UseBaseUrl = false,
            GenerateClientInterfaces = true,
            InjectHttpClient = true,
            DisposeHttpClient = false,
            GenerateExceptionClasses = true,
            ExceptionClass = $"{appName}ClientException",
            ClassName = $"{appName}Client",
            GenerateOptionalParameters = true,
          };

          var generator = new CSharpClientGenerator(document, settings);
          var code = generator.GenerateFile();
          return code;
        }
      );

  private static Task Invoke(string url, DirectoryInfo filePath, SupportedLang language, string appName)
      => language switch
      {
        SupportedLang.Cs => GenerateTypescriptClient(url, filePath, appName),
        SupportedLang.Ts => GenerateCSharpClient(url, filePath, appName),
        _ => throw new ArgumentOutOfRangeException(nameof(language), language, null)
      };

    static async Task Main(string[] args)
    {
      var rc = GetRootCommand();
      rc.Handler = CommandHandler.Create<string, DirectoryInfo, SupportedLang, string>(Invoke);
      await rc.InvokeAsync(args);
    }
  }
}
