using System;

namespace BabelDevirtualizer.Logger
{
    public class ConsoleLogger
    {
        public static ConsoleColor DefaultColor = ConsoleColor.White;

        public static bool isVerbose;

        public static void Verbose(string text, params object[] data)
        {
            if (!isVerbose)
                return;
            Console.ForegroundColor = ConsoleColor.White;
            WriteLine(text, data);
            Console.ForegroundColor = DefaultColor;
        }
        public static void Error(string text, params object[] data)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            WriteLine(text, data);
            Console.ForegroundColor = DefaultColor;
        }
        public static void Warning(string text, params object[] data)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            WriteLine(text, data);
            Console.ForegroundColor = DefaultColor;
        }
        public static void Info(string text, params object[] data)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            WriteLine(text, data);
            Console.ForegroundColor = DefaultColor;
        }
        public static void Success(string text, params object[] data)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            WriteLine(text, data);
            Console.ForegroundColor = DefaultColor;
        }
        public static void WriteLine(string text, params object[] data)
        {
            Console.WriteLine(Escape(string.Format(text, data)));
        }
        static string Escape(string str)
        {
            string result = "";
            foreach (char c in str)
            {
                if (c > 8000)
                    result += "\\u" + Convert.ToString(c, 16).ToUpper();
                else result += c;
            }
            return result;
        }
    }
}
