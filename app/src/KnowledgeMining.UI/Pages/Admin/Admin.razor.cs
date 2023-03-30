﻿using KnowledgeMining.Application.Documents.Queries.GetIndex;
using KnowledgeMining.Domain.Entities;
using KnowledgeMining.UI.Wrappers;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using KnowledgeMining.Application.Documents.Queries.GetDocuments;
using KnowledgeMining.UI.Services.State;
using KnowledgeMining.UI.Models;

namespace KnowledgeMining.UI.Pages.Admin
{
    public partial class Admin
    {
        [Inject] public ISnackbar Snackbar { get; set; }
        [Inject] public IMediator Mediator { get; set; }
        [Inject] public IJSRuntime jsRuntime { get; set; }
        [Inject] public DocumentCartService CartService { get; set; }

        [Parameter] public string Index { get; set; } = default!;


        //UI States
        private bool _documentListIsLoading = true;
        private bool _canNavigateBack = false;

        //UI
        private string _title = string.Empty;

        //Functional
        private DocumentMetadata? _documentMetadata;
        private IndexItem? _indexItem;
        private string? _textToHighlight;
        private DocumentMetadataWrapper? _moreLikeThis;
        private AzureBlobConnector? _azureBlobConnector;
        private List<Document> _errorDocuments;

        private const int PAGESIZE = 100;

        protected override async Task OnInitializedAsync()
        {
            _ = Index ?? throw new ArgumentNullException(nameof(Index));

            await GetErrorBlobs();
            await base.OnInitializedAsync();
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if(firstRender)
            {
                _canNavigateBack = await jsRuntime.InvokeAsync<bool>("HasHistory");
                StateHasChanged();
            }
        }

        protected override Task OnParametersSetAsync()
        {
            if (_documentListIsLoading)
            {
                GetErrorBlobs().ConfigureAwait(false);
            }

            return base.OnParametersSetAsync();
        }

        public override Task SetParametersAsync(ParameterView parameters)
        {
            return base.SetParametersAsync(parameters);
        }

        private async Task GetErrorBlobs()
        {
            var documentResponse = await Mediator.Send(new GetDocumentsQuery("error-documents", null, PAGESIZE, null));
            
            _errorDocuments = new();
            foreach (var document in documentResponse.Documents)
            {
                _errorDocuments.Add(document);
            }

            _documentListIsLoading = false;
        }
    }
}
