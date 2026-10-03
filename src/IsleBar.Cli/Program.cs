using IsleBar.Cli;

// UTF-8 out for the commands people run by hand: on a console with a legacy code page (949, 437) emoji, Korean names and
// "→" printed as "?" (review 10-03). Not for hook / statusline — they share the agent's console, and changing its code page
// would stay after we exit and garble other tools' Korean output. Put back on the way out, too.
var interactive = args.Length == 0 || args[0] is not ("hook" or "statusline");
System.Text.Encoding? before = null;
if (interactive && !Console.IsOutputRedirected)
{
    try
    {
        before = Console.OutputEncoding;
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
    }
    catch (IOException)
    {
        before = null;
    }
}

try
{
    return CommandRunner.Run(args, Console.Out, Console.Error);
}
finally
{
    if (before is not null)
    {
        try
        {
            Console.Out.Flush();
            Console.OutputEncoding = before;
        }
        catch (IOException)
        {
        }
    }
}
