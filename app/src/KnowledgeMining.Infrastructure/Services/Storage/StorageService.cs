﻿using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using KnowledgeMining.Application.Common.Interfaces;
using KnowledgeMining.Application.Common.Options;
using KnowledgeMining.Application.Documents.Queries.GetDocuments;
using KnowledgeMining.Domain.Enums;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using SearchDocument = KnowledgeMining.Application.Documents.Queries.GetDocuments.Document;
using UploadDocument = KnowledgeMining.Application.Documents.Commands.UploadDocument.Document;


namespace KnowledgeMining.Infrastructure.Services.Storage
{
    public class StorageService : IStorageService
    {
        private const int MAX_ITEMS_PER_REQUEST = 5_000;
        private const int DEFAULT_PAGE_SIZE = 10;

        private readonly BlobServiceClient _blobServiceClient;
        private readonly StorageOptions _storageOptions;

        private readonly ILogger<StorageService> _logger;

        public StorageService(BlobServiceClient blobServiceClient,
                                 IOptions<StorageOptions> storageOptions,
                                 ILogger<StorageService> logger)
        {
            _blobServiceClient = blobServiceClient;
            _storageOptions = storageOptions.Value;
            _logger = logger;
        }

        public async Task<GetDocumentsResponse> GetDocuments(string? searchPrefix, int pageSize, string? continuationToken, CancellationToken cancellationToken)
        {
            searchPrefix ??= string.Empty;
            pageSize = pageSize is > 0 and <= MAX_ITEMS_PER_REQUEST ? pageSize : DEFAULT_PAGE_SIZE;

            var pages = GetBlobContainerClient()
                            .GetBlobsAsync(traits: BlobTraits.Metadata | BlobTraits.Tags, prefix: searchPrefix, cancellationToken: cancellationToken)
                            .AsPages(continuationToken, pageSize);

            var iterator = pages.GetAsyncEnumerator(cancellationToken);

            try
            {
                await iterator.MoveNextAsync();

                var page = iterator.Current;

                return new GetDocumentsResponse()
                {
                    Documents = page?.Values?.Select(b => new SearchDocument(b.Name, b.Tags, b.Metadata)) ?? Enumerable.Empty<SearchDocument>(),
                    NextPage = page?.ContinuationToken
                };
            }
            finally
            {
                await iterator.DisposeAsync();
            }
        }

        private async Task SetDocumentMetadata(BlobClient? blob, IDictionary<string, string>? metadata, CancellationToken cancellationToken)
        {
            if (blob == null || metadata == null)
                return;

            try
            {
                var response = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
                UpdateKeyValueData(response.Value.Metadata, metadata);

                await blob.SetMetadataAsync(metadata, cancellationToken: cancellationToken);
            }
            catch (Exception e)
            {
                _logger.LogCritical(e, "Set {DocumentName} index tags {@Metadata} failed.", blob.Name, metadata);
                throw;
            }
        }

        private async Task SetDocumentTags(BlobClient? blob, IDictionary<string, string>? tags, CancellationToken cancellationToken)
        {
            if (blob == null || tags == null)
                return;

            try
            {
                var response = await blob.GetTagsAsync(cancellationToken: cancellationToken);
                UpdateKeyValueData(response.Value.Tags, tags);

                await blob.SetTagsAsync(response.Value.Tags, cancellationToken: cancellationToken);
            }
            catch (Exception e)
            {
                _logger.LogCritical(e, "Set {DocumentName} index tags {@Tags} failed.", blob.Name, tags);
                throw;
            }
        }

        private void UpdateKeyValueData(IDictionary<string, string> source, IDictionary<string, string> updates)
        {
            //add new tags
            var newKeys = updates.Keys.Except(source.Keys);
            foreach (var newKey in newKeys)
                source.Add(newKey, updates[newKey]);

            var existingKeys = updates.Keys.Intersect(source.Keys);
            foreach (var existingKey in existingKeys)
                source[existingKey] = updates[existingKey];
        }

        public async Task SetDocumentTraits(SearchDocument document, DocumentTraits blobTraits, CancellationToken cancellationToken)
        {
            var blob = GetBlobContainerClient()
                .GetBlobClient(document.Name);

            if(blobTraits.HasFlag(DocumentTraits.Metadata))
            {
                await SetDocumentMetadata(blob, document.Metadata, cancellationToken);
            }

            if(blobTraits.HasFlag(DocumentTraits.Tags) && document.Tags != null)
            {
                await SetDocumentTags(blob, document.Tags, cancellationToken);
            }            
        }

        public async Task<IEnumerable<SearchDocument>> UploadDocuments(IEnumerable<UploadDocument> documents, CancellationToken cancellationToken)
        {
            var result = new List<SearchDocument>();

            if (documents.Any())
            {
                var container = GetBlobContainerClient();

                foreach (var file in documents)
                {
                    if (file.Content.Length > 0)
                    {
                        try
                        {
                            var blob = container.GetBlobClient(file.Name);

                            var blobHttpHeader = new BlobHttpHeaders
                            {
                                ContentType = file.ContentType
                            };

                            var uploadFileResult = 
                                await blob.UploadAsync(file.Content, httpHeaders: blobHttpHeader, cancellationToken: cancellationToken).ConfigureAwait(false);

                            if (file.Tags?.Any() ?? false)
                            {
                                var nonEmptyTags = RemoveEmptyTags(file.Tags);
                                await blob.SetTagsAsync(nonEmptyTags, cancellationToken: cancellationToken);
                                await blob.SetMetadataAsync(nonEmptyTags, cancellationToken: cancellationToken);
                            }

                            result.Add(new SearchDocument
                            {
                                Name = file.Name,
                                Tags = file.Tags
                            });
                        }
                        catch(Exception ex)
                        {
                            _logger.LogCritical(ex, "Failed to upload file {FileName}", file.Name);
                        }
                        finally
                        {
                            if (!file.LeaveOpen)
                            {
                                await file.Content.DisposeAsync().ConfigureAwait(false);
                            }
                        }
                    }
                }
            }

            return result;
        }

        private IDictionary<string, string> RemoveEmptyTags(IDictionary<string, string> tags)
        {
            return tags.Where(t => !string.IsNullOrWhiteSpace(t.Key) && !string.IsNullOrWhiteSpace(t.Value)).ToDictionary(t => t.Key, t => t.Value);
        }

        public async ValueTask<byte[]> DownloadDocument(string documentName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(documentName))
            {
                return Array.Empty<byte>();
            }

            var containerName = _storageOptions.ContainerName;
            if (documentName.Contains("/")) //has container
            {
                containerName = documentName.Substring(0, documentName.IndexOf("/"));
                documentName = documentName.Substring(documentName.IndexOf("/") + 1);
            }

            var decodedFilename = WebUtility.UrlDecode(documentName);

            var container = GetBlobContainerClient(containerName);
            var blob = container.GetBlobClient(decodedFilename);

            if (!await blob.ExistsAsync(cancellationToken))
            {
                return Array.Empty<byte>();
            }

            using var ms = new MemoryStream();
            var downloadResult = await blob.DownloadAsync(cancellationToken);

            await downloadResult.Value.Content.CopyToAsync(ms, cancellationToken);
            downloadResult.Value.Dispose();

            return ms.ToArray();
        }

        public async ValueTask DeleteDocument(string documentName, CancellationToken cancellationToken)
        {
            var blobContainer = GetBlobContainerClient();

            await blobContainer.DeleteBlobIfExistsAsync(documentName, cancellationToken: cancellationToken);
        }

        private BlobContainerClient GetBlobContainerClient()
        {
            return GetBlobContainerClient(_storageOptions.ContainerName);
        }

        private BlobContainerClient GetBlobContainerClient(string containerName)
        {
            return _blobServiceClient.GetBlobContainerClient(containerName);
        }
    }
}
