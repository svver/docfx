﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DocAsCode.Build.Engine
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Threading.Tasks;

    using Microsoft.DocAsCode.Common;
    using Microsoft.DocAsCode.DataContracts.Common;
    using Microsoft.DocAsCode.Plugins;
    using Microsoft.DocAsCode.Utility;

    public sealed class DocumentBuildContext : IDocumentBuildContext
    {
        private readonly Dictionary<string, TocInfo> _tableOfContents = new Dictionary<string, TocInfo>(FilePathComparer.OSPlatformSensitiveStringComparer);

        public DocumentBuildContext(string buildOutputFolder) : this(buildOutputFolder, Enumerable.Empty<FileAndType>(), ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, 1) { }

        public DocumentBuildContext(
            string buildOutputFolder,
            IEnumerable<FileAndType> allSourceFiles,
            ImmutableArray<string> externalReferencePackages,
            ImmutableArray<string> xrefMaps,
            int maxParallelism)
        {
            BuildOutputFolder = buildOutputFolder;
            AllSourceFiles = GetAllSourceFiles(allSourceFiles);
            ExternalReferencePackages = externalReferencePackages;
            XRefMapUrls = xrefMaps;
            MaxParallelism = maxParallelism;
        }

        public string BuildOutputFolder { get; }

        public ImmutableArray<string> ExternalReferencePackages { get; }

        public ImmutableArray<string> XRefMapUrls { get; }

        public ImmutableDictionary<string, FileAndType> AllSourceFiles { get; }

        public int MaxParallelism { get; }

        public Dictionary<string, string> FileMap { get; private set; } = new Dictionary<string, string>(FilePathComparer.OSPlatformSensitiveStringComparer);

        public Dictionary<string, XRefSpec> XRefSpecMap { get; private set; } = new Dictionary<string, XRefSpec>();

        public Dictionary<string, HashSet<string>> TocMap { get; private set; } = new Dictionary<string, HashSet<string>>(FilePathComparer.OSPlatformSensitiveStringComparer);

        public HashSet<string> XRef { get; } = new HashSet<string>();

        private ConcurrentDictionary<string, XRefSpec> ExternalXRefSpec { get; set; }

        private List<XRefMap> XRefMaps { get; set; }

        private ConcurrentDictionary<string, object> UnknownUids { get; set; }

        public void ResolveExternalXRefSpec()
        {
            var externalXRefSpec = new Dictionary<string, XRefSpec>();

            // remove internal xref.
            var uidList = XRef.Where(s => !XRefSpecMap.ContainsKey(s)).ToList();

            if (uidList.Count > 0)
            {
                uidList = ResolveByXRefMaps(uidList, externalXRefSpec);
            }
            if (uidList.Count > 0)
            {
                uidList = ResolveByExternalReferencePackages(uidList, externalXRefSpec);
            }

            ExternalXRefSpec = new ConcurrentDictionary<string, XRefSpec>(externalXRefSpec);
            UnknownUids = new ConcurrentDictionary<string, object>(
                from uid in uidList
                select new KeyValuePair<string, object>(uid, null));
        }

        private List<string> ResolveByExternalReferencePackages(List<string> uidList, Dictionary<string, XRefSpec> externalXRefSpec)
        {
            if (ExternalReferencePackages.Length == 0)
            {
                return uidList;
            }

            var oldSpecCount = externalXRefSpec.Count;
            var list = new List<string>();
            using (var externalReferences = new ExternalReferencePackageCollection(ExternalReferencePackages, MaxParallelism))
            {
                foreach (var uid in uidList)
                {
                    var spec = GetExternalReference(externalReferences, uid);
                    if (spec != null)
                    {
                        externalXRefSpec[uid] = spec;
                    }
                    else
                    {
                        list.Add(uid);
                    }
                }
            }

            Logger.LogInfo($"{externalXRefSpec.Count - oldSpecCount} external references found in {ExternalReferencePackages.Length} packages.");
            return list;
        }

        private List<string> ResolveByXRefMaps(List<string> uidList, Dictionary<string, XRefSpec> externalXRefSpec)
        {
            if (XRefMapUrls.Length == 0)
            {
                return uidList;
            }

            var oldSpecCount = externalXRefSpec.Count;
            var list = new List<string>();
            XRefMaps = LoadXRefMaps();

            foreach (var uid in uidList)
            {
                var spec = (from map in XRefMaps select map.Find(uid)).FirstOrDefault();
                if (spec != null)
                {
                    externalXRefSpec[uid] = spec;
                }
                else
                {
                    list.Add(uid);
                }
            }

            Logger.LogInfo($"{externalXRefSpec.Count - oldSpecCount} external references found in {XRefMaps.Count} xref maps.");
            return list;
        }

        private List<XRefMap> LoadXRefMaps()
        {
            using (var client = new HttpClient())
            {
                Logger.LogInfo($"Donwloading xref maps from:{Environment.NewLine}{string.Join(Environment.NewLine, XRefMapUrls)}");
                var mapTasks = (from url in XRefMapUrls
                                select LoadXRefMap(url, client)).ToArray();
                Task.WaitAll(mapTasks);
                return (from t in mapTasks
                        where t.Result != null
                        select t.Result).ToList();
            }
        }

        private async Task<XRefMap> LoadXRefMap(string url, HttpClient client)
        {
            try
            {
                Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri) &&
                    uri.Scheme != "http" &&
                    uri.Scheme != "https")
                {
                    Logger.LogWarning($"Ignore invalid url: {url}");
                    return null;
                }
                using (var stream = await client.GetStreamAsync(uri))
                using (var sr = new StreamReader(stream))
                {
                    var map = YamlUtility.Deserialize<XRefMap>(sr);
                    map.UpdateHref(uri);
                    Logger.LogVerbose($"Xref map ({url}) downloaded.");
                    return map;
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Unable to load xref map from {url}, detail:{Environment.NewLine}{ex.ToString()}");
                return null;
            }
        }

        public string GetFilePath(string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key));
            string filePath;
            if (FileMap.TryGetValue(key, out filePath))
            {
                return filePath;
            }

            return null;
        }

        // TODO: use this method instead of directly accessing FileMap
        public void SetFilePath(string key, string filePath)
        {
            FileMap[key] = filePath;
        }

        // TODO: use this method instead of directly accessing UidMap
        public void RegisterInternalXrefSpec(XRefSpec xrefSpec)
        {
            if (xrefSpec == null) throw new ArgumentNullException(nameof(xrefSpec));
            if (string.IsNullOrEmpty(xrefSpec.Href)) throw new ArgumentException("Href for xref spec must contain value");
            if (!PathUtility.IsRelativePath(xrefSpec.Href)) throw new ArgumentException("Only relative href path is supported");
            XRefSpecMap[xrefSpec.Uid] = xrefSpec;
        }

        public XRefSpec GetXrefSpec(string uid)
        {
            if (string.IsNullOrEmpty(uid)) throw new ArgumentNullException(nameof(uid));

            XRefSpec xref;
            if (XRefSpecMap.TryGetValue(uid, out xref))
            {
                return xref;
            }

            if (ExternalXRefSpec.TryGetValue(uid, out xref))
            {
                return xref;
            }

            if (UnknownUids.ContainsKey(uid))
            {
                return null;
            }

            if (XRefMaps != null && XRefMaps.Count > 0)
            {
                xref = (from map in XRefMaps select map.Find(uid)).FirstOrDefault();
                if (xref != null)
                {
                    ExternalXRefSpec.TryAdd(uid, xref);
                    return xref;
                }
            }

            if (ExternalReferencePackages.Length > 0)
            {
                using (var externalReferences = new ExternalReferencePackageCollection(ExternalReferencePackages, MaxParallelism))
                {
                    xref = GetExternalReference(externalReferences, uid);
                }
                if (xref != null)
                {
                    ExternalXRefSpec.TryAdd(uid, xref);
                    return xref;
                }
            }

            UnknownUids.TryAdd(uid, null);
            return null;
        }

        public IImmutableList<string> GetTocFileKeySet(string key)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentNullException(nameof(key));
            HashSet<string> sets;
            if (TocMap.TryGetValue(key, out sets))
            {
                return sets.ToImmutableArray();
            }

            return null;
        }

        public void RegisterToc(string tocFileKey, string fileKey)
        {
            if (string.IsNullOrEmpty(fileKey)) throw new ArgumentNullException(nameof(fileKey));
            if (string.IsNullOrEmpty(tocFileKey)) throw new ArgumentNullException(nameof(tocFileKey));
            HashSet<string> sets;
            if (TocMap.TryGetValue(fileKey, out sets))
            {
                sets.Add(tocFileKey);
            }
            else
            {
                TocMap[fileKey] = new HashSet<string>(FilePathComparer.OSPlatformSensitiveComparer) { tocFileKey };
            }
        }

        public void RegisterTocInfo(TocInfo toc)
        {
            _tableOfContents[toc.TocFileKey] = toc;
        }

        public IImmutableList<TocInfo> GetTocInfo()
        {
            return _tableOfContents.Values.ToImmutableList();
        }

        private ImmutableDictionary<string, FileAndType> GetAllSourceFiles(IEnumerable<FileAndType> allSourceFiles)
        {
            var dict = new Dictionary<string, FileAndType>(FilePathComparer.OSPlatformSensitiveStringComparer);
            foreach (var item in allSourceFiles)
            {
                var path = (string)((RelativePath)item.File).GetPathFromWorkingFolder();
                FileAndType ft;
                if (dict.TryGetValue(path, out ft))
                {
                    if (FilePathComparer.OSPlatformSensitiveStringComparer.Equals(ft.BaseDir, item.BaseDir) &&
                        FilePathComparer.OSPlatformSensitiveStringComparer.Equals(ft.File, item.File))
                    {
                        if (ft.Type >= item.Type)
                        {
                            Logger.LogWarning($"Ignored duplicate file {Path.Combine(item.BaseDir, item.File)}.");
                            continue;
                        }
                        else
                        {
                            Logger.LogWarning($"Ignored duplicate file {Path.Combine(ft.BaseDir, ft.File)}.");
                        }
                    }
                    else
                    {
                        if (ft.Type >= item.Type)
                        {
                            Logger.LogWarning($"Ignored conflict file {Path.Combine(item.BaseDir, item.File)} for {path} by {Path.Combine(ft.BaseDir, ft.File)}.");
                            continue;
                        }
                        else
                        {
                            Logger.LogWarning($"Ignored conflict file {Path.Combine(ft.BaseDir, ft.File)} for {path} by {Path.Combine(item.BaseDir, item.File)}.");
                        }
                    }
                }
                dict[path] = item;
            }
            return dict.ToImmutableDictionary();
        }

        private static XRefSpec GetExternalReference(ExternalReferencePackageCollection externalReferences, string uid)
        {
            ReferenceViewModel vm;
            if (!externalReferences.TryGetReference(uid, out vm))
            {
                return null;
            }
            using (var sw = new StringWriter())
            {
                YamlUtility.Serialize(sw, vm);
                using (var sr = new StringReader(sw.ToString()))
                {
                    return YamlUtility.Deserialize<XRefSpec>(sr);
                }
            }
        }
    }
}
