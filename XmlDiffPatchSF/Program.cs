﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Xml;
using Microsoft.XmlDiffPatch;
using System.Text;
using System.IO;
using System.Xml.Linq;
using System.Linq;

namespace DiffPatchXmlSF
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            // Help..
            if (args.Length == 1 && args[0] == "?")
            {
                Console.WriteLine(
                    "\nThis utility takes 2 required unnamed parameters, [currentXmlFileFullPath] and [latestXmlFileFullPath], and two optional parameters, [outputFileFullPath], [mergeExistingNodes].\n" +
                    "The first two parameters should be different versions of the *same* configuration file, where the current version is the one you want to patch/merge into the latest version (v1.0 -> v2.0, etc).\n" +
                    "The patch preserves the current version's settings values for elements/attributes that also exist in the latest version.\n" +
                    "If the optional [outputFileFullPath] arg is not provided, then the patched file name will be [latestXmlFileFullPath] appended with \"_patched\" " +
                    "preceding the file extension.\n\n" +
                    "**Note, if you have observer plugins, then you must supply true for [mergeExistingNodes] as the last argument to pull over your plugin settings as part of the merge.**.\n\n" +
                    "Example:\n\n" +
                    "DiffPatchXml \"C:\\repos\\FO\\3.1.26\\configs\\ApplicationManifest.xml\" \"C:\\repos\\FO\\3.3.1\\configs\\ApplicationManifest.xml\"\n");

                return;
            }

            if (args.Length == 0 || args.Length < 2)
            {
                Console.WriteLine("Please pass the right parameters (2), e.g.,\n\n\t DiffPatchXml [currentXmlFileFullPath] [latestXmlFileFullPath]");
                return;
            }

            if (!File.Exists(args[0]) || !File.Exists(args[1]))
            {
                Console.WriteLine("Supplied xml configuration files must exist.");
                return;
            }

            string currentFileVersionPath = args[0];
            string latestFileVersionPath = args[1];

            try
            {
                var currentXml = XDocument.Load(currentFileVersionPath);
                var latestXml = XDocument.Load(latestFileVersionPath);
                currentXml = null;
                latestXml = null;
            }
            catch (XmlException)
            {
                Console.WriteLine("Only XML is supported.");
                return;
            }

            string diffgramFilePath = Path.GetTempFileName();
            string patchedFilePath = Path.Combine(Path.GetDirectoryName(latestFileVersionPath), Path.GetFileNameWithoutExtension(latestFileVersionPath) + "_patched.xml");
            bool mergeExistingNodes = false;

            if (args.Length == 3 && !bool.TryParse(args[2], out mergeExistingNodes))
            {
               patchedFilePath = args[2];
            }
            else if (args.Length == 4)
            {
                _ = bool.TryParse(args[3], out mergeExistingNodes);
                 patchedFilePath = args[2];
            }

            DiffPatchXmlFiles(currentFileVersionPath, latestFileVersionPath, diffgramFilePath, patchedFilePath, mergeExistingNodes);
        }

        private static void DiffPatchXmlFiles(string currentVersionFilePath, string latestVersionFilePath, string diffGramFilePath, string patchedFilePath, bool mergeExistingNodes = false)
        {
            XmlWriterSettings settings = new XmlWriterSettings
            {
                Encoding = Encoding.UTF8,
                NewLineChars = Environment.NewLine,
                NewLineHandling = NewLineHandling.Entitize,
                NamespaceHandling = NamespaceHandling.OmitDuplicates,
                CheckCharacters = true,
                Indent = true,
                DoNotEscapeUriAttributes = true,
                NewLineOnAttributes = false
            };

            XmlWriter output = null;
            try
            {
                output = XmlWriter.Create(diffGramFilePath, settings);
                GenerateXmlDiffGram(currentVersionFilePath, latestVersionFilePath, ref output);
            }
            finally
            {
                output.Dispose();
            }
           
            /*  Remove changed values from old configs to support carry over to new version, which generally (not always) may have new elements. 
                This enables a master config's values to be preserved across app config upgrades of a Service Fabric app's base configuration (AppManifest and Settings). 
                Based on https://stackoverflow.com/questions/14341490/programmatic-xml-diff-merge-in-c-sharp */

            XNamespace xd = "http://schemas.microsoft.com/xmltools/2002/xmldiff";
            var xdoc = XDocument.Load(diffGramFilePath);

            // xd:change -> match -> @DefaultValue is for ApplicationManifest.xml settings values.
            // xd:change -> match -> @Value is for ApplicationManifest.xml's config override sections and Settings.xml's settings values.
            // xd:change -> match -> @EntryPointType is for Polcies node.
            // xd:remove -> remove enables bringing over existing elements from source config (current) - like for plugins - to the target config (latest).
            if (mergeExistingNodes)
            {
                xdoc.Root.Descendants(xd + "remove").Remove();
            }

            xdoc.Root.Descendants(xd + "change")
                        .Where(
                          n => 
                            n.Attribute("match").Value == "@DefaultValue" ||
                            n.Attribute("match").Value == "@Value" ||
                            n.Attribute("match").Value == "@EntryPointType")?.Remove();

            xdoc.Save(diffGramFilePath);

            PatchXml(currentVersionFilePath, diffGramFilePath, patchedFilePath);
        }

        private static void GenerateXmlDiffGram(string originalFile, string newFile, ref XmlWriter diffGramWriter)
        {
            var xmldiff = new XmlDiff(XmlDiffOptions.IgnoreNamespaces | XmlDiffOptions.IgnorePrefixes | XmlDiffOptions.IgnorePI);

            if (xmldiff.Compare(originalFile, newFile, false, diffGramWriter))
            {
                diffGramWriter.WriteRaw(string.Empty);
            }

            diffGramWriter.Close();
        }

        private static void PatchXml(string originalFile, string diffGramFile, string outputFile)
        {
            var sourceDoc = new XmlDocument(new NameTable());
            sourceDoc.Load(originalFile);

            using (var diffgramReader = new XmlTextReader(diffGramFile))
            {
                var xmlpatch = new XmlPatch();
                xmlpatch.Patch(sourceDoc, diffgramReader);
                diffgramReader.Close();
            }

            XmlWriterSettings settings = new XmlWriterSettings
            {
                Encoding = Encoding.UTF8,
                NewLineChars = Environment.NewLine,
                NewLineHandling = NewLineHandling.Entitize,
                NamespaceHandling = NamespaceHandling.OmitDuplicates,
                CheckCharacters = true,
                Indent = true,
                DoNotEscapeUriAttributes = true,
                NewLineOnAttributes = false,
            };

            using (var output = XmlWriter.Create(outputFile, settings))
            {
                sourceDoc.Save(output);
                output.Close();
            }

            File.Delete(diffGramFile);
        }
    }
}
