﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CodeIndex.Common;
using CodeIndex.Files;
using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;

namespace CodeIndex.IndexBuilder
{
    public class CodeIndexBuilder : IDisposable
    {
        public CodeIndexBuilder(string name, LucenePoolLight codeIndexPool, LucenePoolLight hintIndexPool, ILog log)
        {
            name.RequireNotNullOrEmpty(nameof(name));
            codeIndexPool.RequireNotNull(nameof(codeIndexPool));
            hintIndexPool.RequireNotNull(nameof(hintIndexPool));
            log.RequireNotNull(nameof(log));

            Name = name;
            CodeIndexPool = codeIndexPool;
            HintIndexPool = hintIndexPool;
            Log = log;
        }

        public string Name { get; }
        public LucenePoolLight CodeIndexPool { get; }
        public LucenePoolLight HintIndexPool { get; }
        public ILog Log { get; }

        public void InitIndexFolderIfNeeded()
        {
            if (!Directory.Exists(CodeIndexPool.LuceneIndex))
            {
                Log.Info($"Create {Name} index folder {CodeIndexPool.LuceneIndex}");
                Directory.CreateDirectory(CodeIndexPool.LuceneIndex);
            }

            if (!Directory.Exists(HintIndexPool.LuceneIndex))
            {
                Log.Info($"Create {Name} index folder {HintIndexPool.LuceneIndex}");
                Directory.CreateDirectory(HintIndexPool.LuceneIndex);
            }
        }

        public ConcurrentBag<FileInfo> BuildIndexByBatch(IEnumerable<FileInfo> fileInfos, bool needCommit, bool triggerMerge, bool applyAllDeletes, CancellationToken cancellationToken, int batchSize = 10000)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fileInfos.RequireNotNull(nameof(fileInfos));
            batchSize.RequireRange(nameof(batchSize), int.MaxValue, 50);

            var codeDocuments = new ConcurrentBag<Document>();
            var wholeWords = new ConcurrentDictionary<string, int>();
            var hintWords = new ConcurrentDictionary<string, int>();
            var failedIndexFiles = new ConcurrentBag<FileInfo>();
            using var readWriteSlimLock = new ReaderWriterLockSlim();

            Parallel.ForEach(fileInfos, new ParallelOptions { CancellationToken = cancellationToken }, fileInfo =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                readWriteSlimLock.EnterReadLock();
                try
                {
                    if (fileInfo.Exists)
                    {
                        var source = CodeSource.GetCodeSource(fileInfo, FilesContentHelper.ReadAllText(fileInfo.FullName));

                        AddHintWords(hintWords, wholeWords, source.Content);

                        var doc = IndexBuilderHelper.GetDocumentFromSource(source);
                        codeDocuments.Add(doc);

                        Log.Info($"{Name}: Add index For {source.FilePath}");
                    }
                }
                catch (Exception ex)
                {
                    failedIndexFiles.Add(fileInfo);
                    Log.Error($"{Name}: Add index for {fileInfo.FullName} failed, exception: " + ex);
                }
                finally
                {
                    readWriteSlimLock.ExitReadLock();
                }

                if (codeDocuments.Count >= batchSize)
                {
                    readWriteSlimLock.EnterWriteLock();
                    try
                    {
                        if (codeDocuments.Count >= batchSize)
                        {
                            BuildIndex(needCommit, triggerMerge, applyAllDeletes, codeDocuments, hintWords, cancellationToken);
                            codeDocuments.Clear();
                            hintWords.Clear();
                        }
                    }
                    finally
                    {
                        readWriteSlimLock.ExitWriteLock();
                    }
                }
            });

            if (codeDocuments.Count > 0)
            {
                BuildIndex(needCommit, triggerMerge, applyAllDeletes, codeDocuments, hintWords, cancellationToken);
            }

            wholeWords.Clear();

            return failedIndexFiles;
        }

        void AddHintWords(HashSet<string> hintWords, string content)
        {
            var words = WordSegmenter.GetWords(content).Where(word => word.Length > 3 && word.Length < 200);
            foreach (var word in words)
            {
                hintWords.Add(word);
            }
        }

        void AddHintWords(ConcurrentDictionary<string, int> hintWords, ConcurrentDictionary<string, int> wholeWords, string content)
        {
            var words = WordSegmenter.GetWords(content).Where(word => word.Length > 3 && word.Length < 200);
            foreach (var word in words)
            {
                if (wholeWords.TryAdd(word, 0)) // Avoid Distinct Value
                {
                    hintWords.TryAdd(word, 0);
                }
            }
        }

        public void DeleteAllIndex()
        {
            Log.Info($"{Name}: Delete All Index start");
            CodeIndexPool.DeleteAllIndex();
            HintIndexPool.DeleteAllIndex();
            Log.Info($"{Name}: Delete All Index finished");
        }

        public IEnumerable<(string FilePath, DateTime LastWriteTimeUtc)> GetAllIndexedCodeSource()
        {
            return CodeIndexPool.Search(new MatchAllDocsQuery(), int.MaxValue).Select(u => (u.Get(nameof(CodeSource.FilePath)), new DateTime(long.Parse(u.Get(nameof(CodeSource.LastWriteTimeUtc)))))).ToList();
        }

        public IndexBuildResults CreateIndex(FileInfo fileInfo)
        {
            try
            {
                if (fileInfo.Exists)
                {
                    var source = CodeSource.GetCodeSource(fileInfo, FilesContentHelper.ReadAllText(fileInfo.FullName));

                    var words = new HashSet<string>();
                    AddHintWords(words, source.Content);

                    var doc = IndexBuilderHelper.GetDocumentFromSource(source);
                    CodeIndexPool.BuildIndex(new[] { doc }, false);

                    foreach (var word in words)
                    {
                        HintIndexPool.UpdateIndex(new Term(nameof(CodeWord.Word), word), new Document
                        {
                            new StringField(nameof(CodeWord.Word), word, Field.Store.YES),
                            new StringField(nameof(CodeWord.WordLower), word.ToLowerInvariant(), Field.Store.YES)
                        });
                    }

                    Log.Info($"{Name}: Create index For {source.FilePath} finished");
                }

                return IndexBuildResults.Successful;
            }
            catch (Exception ex)
            {
                Log.Error($"{Name}: Create index for {fileInfo.FullName} failed, exception: " + ex);

                if (ex is IOException)
                {
                    return IndexBuildResults.FailedWithIOException;
                }
                else if (ex is OperationCanceledException)
                {
                    throw;
                }

                return IndexBuildResults.FailedWithError;
            }
        }

        void BuildIndex(bool needCommit, bool triggerMerge, bool applyAllDeletes, ConcurrentBag<Document> codeDocuments, ConcurrentDictionary<string, int> words, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Log.Info($"{Name}: Build code index start, documents count {codeDocuments.Count}");

            Parallel.ForEach(
                codeDocuments,
                () => new List<Document>(),
                (codeDocument, status, documentLists) =>
                {
                    documentLists.Add(codeDocument);
                    return documentLists;
                },
                documentLists =>
                {
                    CodeIndexPool.BuildIndex(documentLists, needCommit, triggerMerge, applyAllDeletes);
                });

            Log.Info($"{Name}: Build code index finished");

            Log.Info($"{Name}: Build hint index start, documents count {words.Count}");

            Parallel.ForEach(words, word =>
            {
                HintIndexPool.UpdateIndex(new Term(nameof(CodeWord.Word), word.Key), new Document
                {
                    new StringField(nameof(CodeWord.Word), word.Key, Field.Store.YES),
                    new StringField(nameof(CodeWord.WordLower), word.Key.ToLowerInvariant(), Field.Store.YES)
                });
            });

            if (needCommit || triggerMerge || applyAllDeletes)
            {
                HintIndexPool.Commit();
            }

            Log.Info($"{Name}: Build hint index finished");
        }

        public bool RenameFolderIndexes(string oldFolderPath, string nowFolderPath, CancellationToken cancellationToken)
        {
            try
            {
                var documents = CodeIndexPool.Search(new PrefixQuery(GetNoneTokenizeFieldTerm(nameof(CodeSource.FilePath), oldFolderPath)), 1);

                foreach (var document in documents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RenameIndex(document, oldFolderPath, nowFolderPath);
                }

                Log.Info($"{Name}: Rename folder index from {oldFolderPath} to {nowFolderPath} successful");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"{Name}: Rename folder index from {oldFolderPath} to {nowFolderPath} failed, exception: " + ex);
                return false;
            }
        }

        public IndexBuildResults RenameFileIndex(string oldFilePath, string nowFilePath)
        {
            try
            {
                var documents = CodeIndexPool.Search(new TermQuery(GetNoneTokenizeFieldTerm(nameof(CodeSource.FilePath), oldFilePath)), 1);

                if (documents.Length == 1)
                {
                    RenameIndex(documents[0], oldFilePath, nowFilePath);

                    Log.Info($"{Name}: Rename file index from {oldFilePath} to {nowFilePath} successful");

                    return IndexBuildResults.Successful;
                }

                if (documents.Length == 0)
                {
                    Log.Info($"{Name}: Rename file index failed, unable to find any document from {oldFilePath}, possible template file renamed, fallback to create index.");
                    return CreateIndex(new FileInfo(nowFilePath));
                }

                Log.Warn($"{Name}: Rename file index from {oldFilePath} to {nowFilePath} failed, unable to find one document, there are {documents.Length} document(s) founded");
                return IndexBuildResults.FailedWithError;
            }
            catch (Exception ex)
            {
                Log.Error($"{Name}: Rename file index from {oldFilePath} to {nowFilePath} failed, exception: " + ex);

                if(ex is IOException)
                {
                    return IndexBuildResults.FailedWithIOException;
                }

                return IndexBuildResults.FailedWithError;
            }
        }

        void RenameIndex(Document document, string oldFilePath, string nowFilePath)
        {
            var pathField = document.Get(nameof(CodeSource.FilePath));
            var nowPath = pathField.Replace(oldFilePath, nowFilePath);
            document.RemoveField(nameof(CodeSource.FilePath));
            document.RemoveField(nameof(CodeSource.FilePath) + Constants.NoneTokenizeFieldSuffix);
            document.Add(new TextField(nameof(CodeSource.FilePath), nowPath, Field.Store.YES));
            document.Add(new StringField(nameof(CodeSource.FilePath) + Constants.NoneTokenizeFieldSuffix, nowPath, Field.Store.YES));
            CodeIndexPool.UpdateIndex(new Term(nameof(CodeSource.CodePK), document.Get(nameof(CodeSource.CodePK))), document);
        }

        public bool IsDisposing { get; private set; }

        public void Dispose()
        {
            if (!IsDisposing)
            {
                IsDisposing = true;
                CodeIndexPool.Dispose();
                HintIndexPool.Dispose();
            }
        }

        public IndexBuildResults UpdateIndex(FileInfo fileInfo, CancellationToken cancellationToken)
        {
            try
            {
                if (fileInfo.Exists)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var source = CodeSource.GetCodeSource(fileInfo, FilesContentHelper.ReadAllText(fileInfo.FullName));

                    var words = new HashSet<string>();
                    AddHintWords(words, source.Content);

                    var doc = IndexBuilderHelper.GetDocumentFromSource(source);
                    CodeIndexPool.UpdateIndex(GetNoneTokenizeFieldTerm(nameof(CodeSource.FilePath), source.FilePath), doc);

                    foreach (var word in words)
                    {
                        // TODO: Delete And Add Hint Words

                        HintIndexPool.UpdateIndex(new Term(nameof(CodeWord.Word), word), new Document
                        {
                            new StringField(nameof(CodeWord.Word), word, Field.Store.YES),
                            new StringField(nameof(CodeWord.WordLower), word.ToLowerInvariant(), Field.Store.YES)
                        });
                    }

                    Log.Info($"{Name}: Update index For {source.FilePath} finished");
                }

                return IndexBuildResults.Successful;
            }
            catch (Exception ex)
            {
                Log.Error($"{Name}: Update index for {fileInfo.FullName} failed, exception: " + ex);


                if (ex is IOException)
                {
                    return IndexBuildResults.FailedWithIOException;
                }
                else if (ex is OperationCanceledException)
                {
                    throw;
                }

                return IndexBuildResults.FailedWithError;
            }
        }

        public bool DeleteIndex(string filePath)
        {
            try
            {
                CodeIndexPool.DeleteIndex(GetNoneTokenizeFieldTerm(nameof(CodeSource.FilePath), filePath));
                Log.Info($"{Name}: Delete index For {filePath} finished");

                // TODO: Delete Hint Words

                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"{Name}: Delete index for {filePath} failed, exception: " + ex);
                return false;
            }
        }

        public void Commit()
        {
            CodeIndexPool.Commit();
            HintIndexPool.Commit();
        }

        public Term GetNoneTokenizeFieldTerm(string fieldName, string termValue)
        {
            return new Term($"{fieldName}{Constants.NoneTokenizeFieldSuffix}", termValue);
        }
    }
}
