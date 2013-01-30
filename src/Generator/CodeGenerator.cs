﻿using System.Reflection;
using Cxxi.Generators;
using Cxxi.Passes;
using Cxxi.Types;
using Mono.Options;
using System;
using System.Collections.Generic;
using System.IO;

namespace Cxxi
{
    public class CodeGenerator
    {
        private readonly Options options;
        private Library library;
        private readonly ILibrary transform;
        public TypeDatabase typeDatabase;

        public CodeGenerator(Options options, ILibrary transform)
        {
            this.options = options;
            this.transform = transform;
        }

        public void ParseCode()
        {
            library = new Library(options.OutputNamespace, options.LibraryName);

            Console.WriteLine("Parsing code...");

            var headers = new List<string>();
            transform.SetupHeaders(headers);

            foreach (var header in headers)
                ParseHeader(header);

            foreach (var header in options.Headers)
                ParseHeader(header);
        }

        void ParseHeader(string file)
        {
            var parserOptions = new ParserOptions
                {
                    Library = library,
                    Verbose = false,
                    IncludeDirs = options.IncludeDirs,
                    FileName = file
                };

            if (!ClangParser.Parse(parserOptions))
            {
                Console.WriteLine("  Could not parse '" + file + "'.");
                return;
            }

            Console.WriteLine("  Parsed '" + file + "'.");
        }

        public void ProcessCode()
        {
            typeDatabase = new TypeDatabase();
            typeDatabase.SetupTypeMaps();

            // Sort the declarations to be in original order.
            foreach (var unit in library.TranslationUnits)
                SortDeclarations(unit);

            if (transform != null)
                transform.Preprocess(new LibraryHelpers(library));

            var passes = new PassBuilder(library);
            passes.ResolveIncompleteDecls(typeDatabase);
            passes.CleanInvalidDeclNames();

            if (transform != null)
                transform.SetupPasses(passes);

            var transformer = new Transform() { Options = options, Passes = passes };
            transformer.TransformLibrary(library);

            if (transform != null)
                transform.Postprocess(new LibraryHelpers(library));
        }

        private static void SortDeclarations(Namespace @namespace)
        {
            @namespace.Classes.Sort((c, c1) =>
                              (int) (c.DefinitionOrder - c1.DefinitionOrder));

            foreach (var childNamespace in @namespace.Namespaces)
                SortDeclarations(childNamespace);
        }

        public void GenerateCode()
        {
            if (library.TranslationUnits.Count <= 0)
                return;

            Console.WriteLine("Generating wrapper code...");

            var gen = new Generator(options, library, transform, typeDatabase);
            gen.Generate();
        }
    }

    public class Options
    {
        public Options()
        {
            Defines = new List<string>();
            IncludeDirs = new List<string>();
            Headers = new List<string>();
            Assembly = string.Empty;
        }

        public bool Verbose = false;
        public bool ShowHelpText = false;
        public bool OutputDebug = false;
        public string OutputNamespace;
        public string OutputDir;
        public string LibraryName;
        public List<string> Defines;
        public List<string> IncludeDirs;
        public List<string> Headers;
        public string Template;
        public string Assembly;
    }

    public class Program
    {
        static void ShowHelp(OptionSet options)
        {
            var module = System.Diagnostics.Process.GetCurrentProcess().MainModule;
            var exeName = Path.GetFileName(module.FileName);
            Console.WriteLine("Usage: " + exeName + " [options]+ headers");
            Console.WriteLine("Generates .NET bindings from C/C++ header files.");
            Console.WriteLine();
            Console.WriteLine("Options:");
            options.WriteOptionDescriptions(Console.Out);
        }

        static bool ParseCommandLineOptions(String[] args, Options options)
        {
            var set = new OptionSet()
                {
                    // Parser options
                    { "D|defines=", v => options.Defines.Add(v) },
                    { "I|include=", v => options.IncludeDirs.Add(v) },
                    // Generator options
                    { "ns|namespace=", v => options.OutputNamespace = v },
                    { "o|outdir=", v => options.OutputDir = v },
                    { "debug", v => options.OutputDebug = true },
                    { "lib|library=", v => options.LibraryName = v },
                    { "t|template=", v => options.Template = v },
                    { "a|assembly=", v => options.Assembly = v },
                    // Misc. options
                    { "v|verbose",  v => { options.Verbose = true; } },
                    { "h|?|help",   v => options.ShowHelpText = v != null },
                };

            if (args.Length == 0 || options.ShowHelpText)
            {
                ShowHelp(set);
                return false;
            }

            try
            {
                options.Headers = set.Parse(args);
            }
            catch (OptionException)
            {
                Console.WriteLine("Error parsing the command line.");
                ShowHelp(set);
                return false;
            }

            return true;
        }

        static bool ParseLibraryAssembly(string path, out ILibrary library)
        {
            library = null;

            if (string.IsNullOrWhiteSpace(path))
            {
                Console.WriteLine("Error: no assembly provided");
                return false;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);

                var assembly = Assembly.LoadFile(fullPath);
                var types = assembly.FindDerivedTypes(typeof(ILibrary));

                foreach (var type in types)
                {
                    var attrs = type.GetCustomAttributes<LibraryTransformAttribute>();
                    if (attrs == null) continue;

                    Console.WriteLine("Found library transform: {0}", type.Name);
                    library = (ILibrary)Activator.CreateInstance(type);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: assembly '{0}' could not be loaded", path);
                return false;
            }

            return true;
        }

        public static void Main(String[] args)
        {
            var options = new Options();

            if (!ParseCommandLineOptions(args, options))
                return;

            ILibrary library = null;
            if (!ParseLibraryAssembly(options.Assembly, out library))
                return;

            var codeGenerator = new CodeGenerator(options, library);
            codeGenerator.ParseCode();
            codeGenerator.ProcessCode();
            codeGenerator.GenerateCode();
        }
    }
}