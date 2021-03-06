// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using dia2;
using Microsoft.Framework.Logging;
using Microsoft.Framework.Runtime;
using Microsoft.Framework.Runtime.Compilation;
using Microsoft.Framework.TestAdapter;

namespace Microsoft.Framework.TestHost.TestAdapter
{
    public class SourceInformationProvider : ISourceInformationProvider
    {
        private readonly IMetadataProjectReference _project;
        private readonly ILogger _logger;

        private bool? _isInitialized;
        private IDiaDataSource _diaDataSource;
        private IDiaSession _diaSession;
        private AssemblyData _assemblyData;

        public SourceInformationProvider(
            IMetadataProjectReference project,
            ILogger logger)
        {
            _project = project;
            _logger = logger;
        }

        public SourceInformation GetSourceInformation(string className, string methodName)
        {
            if (!EnsureInitialized())
            {
                // Unable to load DIA or we had a failure reading the symbols.
                return null;
            }

            Debug.Assert(_diaSession != null);
            Debug.Assert(_assemblyData != null);

            // The DIA code doesn't include a + for nested classes, just a dot.
            var symbolId = FindMethodSymbolId(className.Replace('+', '.'), methodName);
            if (symbolId == null)
            {
                // No matching method in the symbol.
                return null;
            }

            try
            {
                return GetSourceInformation(symbolId.Value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to access source information in symbol.", ex);
                return null;
            }
        }

        private uint? FindMethodSymbolId(string className, string methodName)
        {
            Debug.Assert(_isInitialized == true);

            ClassData classData;
            if (_assemblyData.Classes.TryGetValue(className, out classData))
            {
                MethodData methodData;
                if (classData.Methods.TryGetValue(methodName, out methodData))
                {
                    return methodData.SymbolId;
                }
            }

            return null;
        }

        private SourceInformation GetSourceInformation(uint symbolId)
        {
            Debug.Assert(_isInitialized == true);

            string filename = null;
            int? lineNumber = null;

            IDiaSymbol diaSymbol;
            _diaSession.symbolById(symbolId, out diaSymbol);
            if (diaSymbol == null)
            {
                // Doesn't seem like this should happen, since DIA gave us the id.
                return null;
            }

            IDiaEnumLineNumbers diaLineNumbers;
            _diaSession.findLinesByAddr(
                diaSymbol.addressSection,
                diaSymbol.addressOffset,
                (uint)diaSymbol.length,
                out diaLineNumbers);

            // Resist the user to use foreach here. It doesn't work well with these APIs.
            IDiaLineNumber diaLineNumber;
            var lineNumbersFetched = 0u;

            diaLineNumbers.Next(1u, out diaLineNumber, out lineNumbersFetched);
            while (lineNumbersFetched == 1 && diaLineNumber != null)
            {
                if (filename == null)
                {
                    var diaFile = diaLineNumber.sourceFile;
                    if (diaFile != null)
                    {
                        filename = diaFile.fileName;
                    }
                }

                if (diaLineNumber.lineNumber != 16707566u)
                {
                    // We'll see multiple line numbers for the same method, but we just want the first one.
                    lineNumber = Math.Min(lineNumber ?? Int32.MaxValue, (int)diaLineNumber.lineNumber);
                }

                diaLineNumbers.Next(1u, out diaLineNumber, out lineNumbersFetched);
            }

            if (filename == null || lineNumber == null)
            {
                return null;
            }
            else
            {
                return new SourceInformation(filename, lineNumber.Value);
            }
        }

        private bool EnsureInitialized()
        {
            if (_isInitialized.HasValue)
            {
                return _isInitialized.Value;
            }

            if (_project == null)
            {
                _logger.LogWarning("No project information. No source information will be available.");
                _isInitialized = false;
                return _isInitialized.Value;
            }

            try
            {
                _diaDataSource = (IDiaDataSource)new DiaDataSource();
                _isInitialized = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to create DIA DataSource. No source information will be available.", ex);
                _isInitialized = false;
                return _isInitialized.Value;
            }

            // We have a project, and we successfully loaded DIA, so let's capture the symbols
            // and create a session.
            try
            {
                var context = new CapturingLoadContext();
                _project.Load(context);

                _diaDataSource.loadDataFromIStream(new StreamWrapper(context.Symbols));
                _diaDataSource.openSession(out _diaSession);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to load symbols. No source information will be available.", ex);
                _isInitialized = false;
                return _isInitialized.Value;
            }

            try
            {
                _assemblyData = FetchSymbolData(_diaSession);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to read symbols. No source information will be available.", ex);
                _isInitialized = false;
                return _isInitialized.Value;
            }

            _isInitialized = true;
            return _isInitialized.Value;
        }

        // Builds a lookup table of class+method name.
        //
        // It's easier to build it at once by enumerating, once we have the table, we
        // can use the symbolIds to look up the sources when we need them.
        private static AssemblyData FetchSymbolData(IDiaSession session)
        {
            // This will be a *flat* enumerator of all classes.
            // 
            // A nested class will not contain a '+' in it's name, just a '.' separating the parent class name from
            // the child class name.
            IDiaEnumSymbols diaClasses;

            session.findChildren(
                session.globalScope, // Search at the top-level.
                SymTagEnum.SymTagCompiland, // Just find classes.
                name: null, // Don't filter by name.
                compareFlags: 0u, // doesn't matter because name is null.
                ppResult: out diaClasses);

            var assemblyData = new AssemblyData();

            // Resist the urge to use foreach here. It doesn't work well with these APIs.
            var classesFetched = 0u;
            IDiaSymbol diaClass;

            diaClasses.Next(1u, out diaClass, out classesFetched);
            while (classesFetched == 1 && diaClass != null)
            {
                var classData = new ClassData()
                {
                    Name = diaClass.name,
                    SymbolId = diaClass.symIndexId,
                };
                assemblyData.Classes.Add(diaClass.name, classData);

                IDiaEnumSymbols diaMethods;
                session.findChildren(
                    diaClass,
                    SymTagEnum.SymTagFunction,
                    name: null, // Don't filter by name.
                    compareFlags: 0u, // doesn't matter because name is null.
                    ppResult: out diaMethods);

                // Resist the urge to use foreach here. It doesn't work well with these APIs.
                var methodsFetched = 0u;
                IDiaSymbol diaMethod;

                diaMethods.Next(1u, out diaMethod, out methodsFetched);
                while (methodsFetched == 1 && diaMethod != null)
                {
                    classData.Methods[diaMethod.name] = new MethodData()
                    {
                        Name = diaMethod.name,
                        SymbolId = diaMethod.symIndexId,
                    };

                    diaMethods.Next(1u, out diaMethod, out methodsFetched);
                }

                diaClasses.Next(1u, out diaClass, out classesFetched);
            }

            return assemblyData;
        }

        private class AssemblyData
        {
            public IDictionary<string, ClassData> Classes { get; } = new Dictionary<string, ClassData>();
        }

        private class ClassData
        {
            public string Name { get; set; }

            public uint SymbolId { get; set; }

            public IDictionary<string, MethodData> Methods { get; } = new Dictionary<string, MethodData>();
        }

        private class MethodData
        {
            public string Name { get; set; }

            public uint SymbolId { get; set; }
        }

        private class CapturingLoadContext : IAssemblyLoadContext
        {
            public Stream Symbols { get; set; }

            public void Dispose()
            {
            }

            public Assembly Load(AssemblyName assemblyName)
            {
                throw new NotImplementedException();
            }

            public Assembly LoadFile(string path)
            {
                throw new NotImplementedException();
            }

            public Assembly LoadStream(Stream assemblyStream, Stream assemblySymbols)
            {
                // The stream that's passed to us will get disposed, so we need to make
                // a copy.
                Symbols = new MemoryStream((int)assemblySymbols.Length);
                assemblySymbols.CopyTo(Symbols);

                return null;
            }
        }
    }
}