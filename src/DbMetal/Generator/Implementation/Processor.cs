#region MIT license
// 
// MIT license
//
// Copyright (c) 2007-2008 Jiri Moudry, Pascal Craponne
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
// 
#endregion
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DbLinq.Factory;
using DbLinq.Schema;
using DbLinq.Schema.Dbml;
using DbLinq.Util;
using DbLinq.Vendor;
using DbMetal.Schema;

using Mono.Options;

namespace DbMetal.Generator.Implementation
{
#if !MONO_STRICT
    public
#endif
    class Processor : IProcessor
    {
        private TextWriter log;
        /// <summary>
        /// Log output
        /// </summary>
        public TextWriter Log
        {
            get { return log ?? Console.Out; }
            set
            {
                log = value;
                SchemaLoaderFactory.Log = value;
            }
        }

        public ISchemaLoaderFactory SchemaLoaderFactory { get; set; }

        public Processor()
        {
            //for DbMetal for want to log to console
            //for VisualMetal we want to log to log4net
            //Logger = ObjectFactory.Get<ILogger>();
            SchemaLoaderFactory = ObjectFactory.Get<ISchemaLoaderFactory>();
        }

        public void Process(string[] args)
        {
            var parameters = new Parameters { Log = Log };

            parameters.WriteHeader();

            try
            {
                parameters.Parse(args);
            }
            catch (Exception e)
            {
                Output.WriteErrorLine(Log, e.Message);
                PrintUsage(parameters);
                return;
            }

            if (args.Length == 0 || parameters.Help)
            {
                PrintUsage(parameters);
                return;
            }

            ProcessSchema(parameters);

            if (parameters.Readline)
            {
                // '-readLineAtExit' flag: useful when running from Visual Studio
                Console.ReadKey();
            }
        }

        private void ProcessSchema(Parameters parameters)
        {
            try
            {
                // we always need a factory, even if generating from a DBML file, because we need a namespace
                ISchemaLoader schemaLoader;
                // then we load the schema
                var dbSchema = ReadSchema(parameters, out schemaLoader);

                if (!SchemaIsValid(dbSchema))
                    return;

                // the we write it (to DBML or code)
                WriteSchema(dbSchema, schemaLoader, parameters);
            }
            catch (Exception ex)
            {
                string assemblyName = System.Reflection.Assembly.GetExecutingAssembly().GetName().Name;
                Log.WriteErrorLine(assemblyName + ": {0}", parameters.Debug ? ex.ToString() : ex.Message);
            }
        }

        bool SchemaIsValid(Database database)
        {
            bool error = false;
            foreach (var table in database.Tables)
            {
                error = ValidateAssociations(database, table) || error;
            }
            return !error;
        }

        bool ValidateAssociations(Database database, Table table)
        {
            bool error = false;
            foreach (var association in table.Type.Associations)
            {
                var otherType           = database.Tables.Single(t => t.Type.Name == association.Type).Type;
                var otherAssociation    = otherType.Associations.FirstOrDefault(a => a.Type == table.Type.Name && a.ThisKey == association.OtherKey);
                var otherColumn         = otherType.Columns.Single(c => c.Member == association.OtherKey);

                if (association.CardinalitySpecified && association.Cardinality == Cardinality.Many && association.IsForeignKey)
                {
                    error = true;
                    Log.WriteErrorLine("Error DBML1059: The IsForeignKey attribute of the Association element '{0}' of the Type element '{1}' cannnot be '{2}' when the Cardinality attribute is '{3}'.",
                            association.Name, table.Type.Name, association.IsForeignKey, association.Cardinality);
                }
            }
            return error;
        }

        protected void WriteSchema(Database dbSchema, ISchemaLoader schemaLoader, Parameters parameters)
        {
            if (parameters.Dbml != null)
            {
                //we are supposed to write out a DBML file and exit
                parameters.Write("<<< Writing file '{0}'", parameters.Dbml);
                using (Stream dbmlFile = File.Create(parameters.Dbml))
                {
                    DbmlSerializer.Write(dbmlFile, dbSchema);
                }
            }
            else
            {
                if (!parameters.Schema)
                    RemoveSchemaFromTables(dbSchema);

                // extract filename from output filename, database schema or schema name
                string filename = parameters.Code;
                if (string.IsNullOrEmpty(filename) && !string.IsNullOrEmpty(parameters.Database))
                    filename = parameters.Database.Replace("\"", "");
                if (string.IsNullOrEmpty(filename))
                    filename = dbSchema.Name;

                parameters.Write("<<< writing C# classes into file '{0}'", filename);
                GenerateCode(parameters, dbSchema, schemaLoader, filename);

            }
        }

        protected void RemoveSchemaFromTables(Database schema)
        {
            foreach (var table in schema.Table)
            {
                string[] nameAndSchema = table.Name.Split('.');
                table.Name = nameAndSchema[nameAndSchema.Length - 1];
            }
        }

        public virtual IEnumerable<ICodeGenerator> EnumerateCodeGenerators()
        {
            foreach (var codeGeneratorType in ObjectFactory.Current.GetImplementations(typeof(ICodeGenerator)))
            {
                yield return (ICodeGenerator)ObjectFactory.Current.Get(codeGeneratorType);
            }
        }

        protected virtual ICodeGenerator FindCodeGeneratorByLanguage(string languageCode)
        {
            return (from codeGenerator in EnumerateCodeGenerators()
                    where codeGenerator.LanguageCode == languageCode
                    select codeGenerator).SingleOrDefault();
        }

        protected virtual ICodeGenerator FindCodeGeneratorByExtension(string extension)
        {
            return EnumerateCodeGenerators().SingleOrDefault(gen => gen.Extension == extension.ToLowerInvariant());
        }

        public virtual ICodeGenerator FindCodeGenerator(Parameters parameters, string filename)
        {
            if (!string.IsNullOrEmpty(parameters.Language))
                return FindCodeGeneratorByLanguage(parameters.Language);
            return FindCodeGeneratorByExtension(Path.GetExtension(filename));
        }

        public void GenerateCode(Parameters parameters, Database dbSchema, ISchemaLoader schemaLoader, string filename)
        {
            ICodeGenerator codeGenerator = FindCodeGenerator(parameters, filename)
                                           ?? (string.IsNullOrEmpty(parameters.Language)
                                                   ? CodeDomGenerator.CreateFromFileExtension(
                                                       Path.GetExtension(filename))
                                                   : CodeDomGenerator.CreateFromLanguage(parameters.Language));

            if (!(codeGenerator is CodeDomGenerator))
            {
                parameters.Write("Wrong codeGenerator (CodeDomGenerator is needed)");
                return;
            }

            /*
            if (string.IsNullOrEmpty(filename))
                filename = dbSchema.Class;
            if (String.IsNullOrEmpty(Path.GetExtension(filename)))
                filename += codeGenerator.Extension;
                */

            var generationContext = new GenerationContext(parameters, schemaLoader);

            // EfContext
            filename = dbSchema.Class.Replace("Context", "EfContext.cs");

            using (var streamWriter = new StreamWriter(filename))
            {
                ((CodeDomGenerator)codeGenerator).WriteEfContext(streamWriter, dbSchema, generationContext);
            }

            this.ProcessFile(filename);

            // Entities
            string efFileName = dbSchema.Class.Replace("Context", "Entities.cs");
            parameters.Write("<<< writing EF models into file '{0}'", efFileName);

            using (var streamWriteref = new StreamWriter(efFileName))
            {
                ((CodeDomGenerator)codeGenerator).WriteEf(streamWriteref, dbSchema, generationContext);
            }

            this.ProcessFile(efFileName, true);

            // Generate Repository into separate files, if it's needed
            if (parameters.IContext)
            {
                // IRepository
                string interfaceFileName = "I" + dbSchema.Class.Replace("Context", "Repository.cs");
                parameters.Write("<<< writing IRepository into file '{0}'", interfaceFileName);

                using (var streamWriterIContext = new StreamWriter(interfaceFileName))
                {
                    ((CodeDomGenerator)codeGenerator).WriteIRepository(streamWriterIContext, dbSchema, generationContext);
                }

                this.ProcessFile(interfaceFileName);

                // Repository
                string repoFileName = dbSchema.Class.Replace("Context", "Repository.cs");
                parameters.Write("<<< writing Repository into file '{0}'", repoFileName);

                using (var streamWriterRepo = new StreamWriter(repoFileName))
                {
                    ((CodeDomGenerator)codeGenerator).WriteRepository(streamWriterRepo, dbSchema, generationContext);
                }

                this.ProcessFile(repoFileName);

                // MockRepository
                string mockFileName = "Mock" + dbSchema.Class.Replace("Context", "Repository.cs");

                parameters.Write("<<< writing MockContext into file '{0}'", mockFileName);

                using (var streamWriterMockContext = new StreamWriter(mockFileName))
                {
                    ((CodeDomGenerator)codeGenerator).WriteMockRepository(
                        streamWriterMockContext,
                        dbSchema,
                        generationContext);
                }

                this.ProcessFile(mockFileName);
            }
        }

        public Database ReadSchema(Parameters parameters, out ISchemaLoader schemaLoader)
        {
            Database dbSchema;
            var nameAliases = NameAliasesLoader.Load(parameters.Aliases);
            if (parameters.SchemaXmlFile == null) // read schema from DB
            {
                schemaLoader = SchemaLoaderFactory.Load(parameters);

                parameters.Write(">>> Reading schema from {0} database", schemaLoader.Vendor.VendorName);

                dbSchema = schemaLoader.Load(parameters.Database, nameAliases,
                    new NameFormat(parameters.Pluralize, GetCase(parameters), new CultureInfo(parameters.Culture)),
                    parameters.Sprocs, parameters.Namespace, parameters.Namespace, parameters.ContextNameMode.ToLower());
                dbSchema.Provider = parameters.Provider;
                dbSchema.Tables.Sort(new LambdaComparer<Table>((x, y) => (x.Type.Name.CompareTo(y.Type.Name))));
                foreach (var table in dbSchema.Tables)
                    table.Type.Columns.Sort(new LambdaComparer<Column>((x, y) => (x.Member.CompareTo(y.Member))));
                dbSchema.Functions.Sort(new LambdaComparer<Function>((x, y) => (x.Method.CompareTo(y.Method))));
                //SchemaPostprocess.PostProcess_DB(dbSchema);
            }
            else // load DBML
            {
                dbSchema = ReadSchema(parameters, parameters.SchemaXmlFile);
                parameters.Provider = parameters.Provider ?? dbSchema.Provider;
                schemaLoader = SchemaLoaderFactory.Load(parameters);
            }

            if (schemaLoader == null)
                throw new ApplicationException("Please provide -Provider=MySql (or Oracle, OracleODP, PostgreSql, Sqlite - see app.config for provider listing)");

            return dbSchema;
        }

        public Database ReadSchema(Parameters parameters, string filename)
        {
            parameters.Write(">>> Reading schema from DBML file '{0}'", filename);
            using (Stream dbmlFile = File.OpenRead(filename))
            {
                return DbmlSerializer.Read(dbmlFile);
            }
        }

        private void ProcessFile(string filePath, bool replaceToVirtual = false)
        {
            string text = File.ReadAllText(filePath);

            text = text.Replace("{ get; set; };", "{ get; set; }");

            if (replaceToVirtual)
            {
                text = text.Replace("internal static", "public virtual");
            }

            text = text.Replace(";\r\n\t\r\n\t", ";\r\n\t");
            text = text.Replace("{\r\n\t\t\r\n\t\t", "{\r\n\t\t");
            text = text.Replace(";\r\n\t\t\t\tif ", ";\r\n\t\t\t\t\r\n\t\t\t\tif ");
            text = text.Replace("}\r\n\t\t\t\tsb ", "}\r\n\t\t\t\t\r\n\t\t\t\tsb ");
            text = text.Replace("\r\n\t\t{\r\n\t\t\tget;\r\n\t\t}", " { get; }");
            text = text.Replace("\t", "    ");

            File.WriteAllText(filePath, text);
        }

        private void PrintUsage(Parameters parameters)
        {
            parameters.WriteHelp();
        }

        private Case GetCase(Parameters parameters)
        {
            if (String.IsNullOrEmpty(parameters.Case))
                return Case.PascalCase;

            switch (parameters.Case.ToLowerInvariant())
            {
                case "leave":
                    return Case.Leave;
                case "camel":
                    return Case.camelCase;
                case "pascal":
                    return Case.PascalCase;
                default:
                    return Case.NetCase;
            }
        }
    }
}
