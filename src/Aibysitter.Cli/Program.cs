namespace Aibysitter.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        Aibysitter.Rules.RegexTimeout.Apply();
        return CliApp.Run(args, Console.In, Console.Out, Console.Error);
    }
}
