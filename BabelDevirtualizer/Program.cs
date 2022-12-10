using BabelDevirtualizer.Core;
using BabelDevirtualizer.Logger;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace BabelDevirtualizer
{
    class Program
    {
        [DllImport("msvcrt.dll")]
        static extern int system(string cmd);

        static void Main(string[] args)
        {
            Console.InputEncoding = Console.OutputEncoding = Encoding.Unicode;
            if (args.Length > 0)
                ConsoleLogger.isVerbose = Regex.IsMatch(args[args.Length - 1], "^(-?-|/|)(v|V)");
            if (ConsoleLogger.isVerbose)
                ConsoleLogger.Verbose("Verbose mode: on");
            if (args.Length == 0)
            {
                args = new string[1];
                Console.Write("Path to file for devirtualize: ");
                args[0] = Console.ReadLine().Replace("\"", "");
            }
            for (int i = 0; i < args.Length - (ConsoleLogger.isVerbose ? 1 : 0); i++)
            {
                string path = args[i];
                string outputFilePath;
                try
                {
                    outputFilePath = $"{Path.GetDirectoryName(path)}\\{Path.GetFileNameWithoutExtension(path)}-Devirtualized{Path.GetExtension(path)}";
                    using (MethodDevirtualizer md = new MethodDevirtualizer(path))
                    {
                        md.Run();
                        md.Write(outputFilePath);
                    }
                }
                catch (Exception ex)
                {
                    ConsoleLogger.Error("Error: Cannot load the file!");
                    ConsoleLogger.Verbose(ex.ToString());
                    continue;
                }
            }
            system("pause");
        }
    }
}
