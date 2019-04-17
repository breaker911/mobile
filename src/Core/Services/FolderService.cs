﻿using Bit.Core.Abstractions;
using Bit.Core.Enums;
using Bit.Core.Exceptions;
using Bit.Core.Models.Data;
using Bit.Core.Models.Domain;
using Bit.Core.Models.Request;
using Bit.Core.Models.Response;
using Bit.Core.Models.View;
using Bit.Core.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Bit.Core.Services
{
    public class FolderService
    {
        private const string Keys_CiphersFormat = "ciphers_{0}";
        private const string Keys_FoldersFormat = "folders_{0}";
        private const string NestingDelimiter = "/";

        private List<FolderView> _decryptedFolderCache;
        private readonly ICryptoService _cryptoService;
        private readonly IUserService _userService;
        private readonly IApiService _apiService;
        private readonly IStorageService _storageService;
        private readonly II18nService _i18nService;
        private readonly ICipherService _cipherService;

        public FolderService(
            ICryptoService cryptoService,
            IUserService userService,
            IApiService apiService,
            IStorageService storageService,
            II18nService i18nService,
            ICipherService cipherService)
        {
            _cryptoService = cryptoService;
            _userService = userService;
            _apiService = apiService;
            _storageService = storageService;
            _i18nService = i18nService;
            _cipherService = cipherService;
        }

        public void ClearCache()
        {
            _decryptedFolderCache = null;
        }

        public async Task<Folder> EncryptAsync(FolderView model, SymmetricCryptoKey key = null)
        {
            var folder = new Folder
            {
                Id = model.Id,
                Name = await _cryptoService.EncryptAsync(model.Name, key)
            };
            return folder;
        }

        public async Task<Folder> GetAsync(string id)
        {
            var userId = await _userService.GetUserIdAsync();
            var folders = await _storageService.GetAsync<Dictionary<string, FolderData>>(
                string.Format(Keys_FoldersFormat, userId));
            if(!folders?.ContainsKey(id) ?? true)
            {
                return null;
            }
            return new Folder(folders[id]);
        }

        public async Task<List<Folder>> GetAllAsync()
        {
            var userId = await _userService.GetUserIdAsync();
            var folders = await _storageService.GetAsync<Dictionary<string, FolderData>>(
                string.Format(Keys_CiphersFormat, userId));
            var response = folders.Select(f => new Folder(f.Value));
            return response.ToList();
        }

        // TODO: sequentialize?
        public async Task<List<FolderView>> GetAllDecryptedAsync()
        {
            if(_decryptedFolderCache != null)
            {
                return _decryptedFolderCache;
            }
            var hashKey = await _cryptoService.HasKeyAsync();
            if(!hashKey)
            {
                throw new Exception("No key.");
            }
            var decFolders = new List<FolderView>();
            var tasks = new List<Task>();
            var folders = await GetAllAsync();
            foreach(var folder in folders)
            {
                tasks.Add(folder.DecryptAsync().ContinueWith(async f => decFolders.Add(await f)));
            }
            await Task.WhenAll(tasks);
            decFolders = decFolders.OrderBy(f => f, new FolderLocaleComparer(_i18nService)).ToList();

            var noneFolder = new FolderView
            {
                Name = _i18nService.T("noneFolder")
            };
            decFolders.Add(noneFolder);

            _decryptedFolderCache = decFolders;
            return _decryptedFolderCache;
        }

        // TODO: nested stuff

        public async Task SaveWithServerAsync(Folder folder)
        {
            var request = new FolderRequest(folder);
            FolderResponse response;
            if(folder.Id == null)
            {
                response = await _apiService.PostFolderAsync(request);
                folder.Id = response.Id;
            }
            else
            {
                response = await _apiService.PutFolderAsync(folder.Id, request);
            }
            var userId = await _userService.GetUserIdAsync();
            var data = new FolderData(response, userId);
            await UpsertAsync(data);
        }

        public async Task UpsertAsync(FolderData folder)
        {
            var userId = await _userService.GetUserIdAsync();
            var storageKey = string.Format(Keys_FoldersFormat, userId);
            var folders = await _storageService.GetAsync<Dictionary<string, FolderData>>(storageKey);
            if(folders == null)
            {
                folders = new Dictionary<string, FolderData>();
            }
            if(!folders.ContainsKey(folder.Id))
            {
                folders.Add(folder.Id, null);
            }
            folders[folder.Id] = folder;
            await _storageService.SaveAsync(storageKey, folders);
            _decryptedFolderCache = null;
        }

        public async Task UpsertAsync(List<FolderData> folder)
        {
            var userId = await _userService.GetUserIdAsync();
            var storageKey = string.Format(Keys_FoldersFormat, userId);
            var folders = await _storageService.GetAsync<Dictionary<string, FolderData>>(storageKey);
            if(folders == null)
            {
                folders = new Dictionary<string, FolderData>();
            }
            foreach(var f in folder)
            {
                if(!folders.ContainsKey(f.Id))
                {
                    folders.Add(f.Id, null);
                }
                folders[f.Id] = f;
            }
            await _storageService.SaveAsync(storageKey, folders);
            _decryptedFolderCache = null;
        }

        public async Task ReplaceAsync(Dictionary<string, FolderData> folders)
        {
            var userId = await _userService.GetUserIdAsync();
            await _storageService.SaveAsync(string.Format(Keys_FoldersFormat, userId), folders);
            _decryptedFolderCache = null;
        }

        public async Task ClearAsync(string userId)
        {
            await _storageService.RemoveAsync(string.Format(Keys_FoldersFormat, userId));
            _decryptedFolderCache = null;
        }

        public async Task DeleteAsync(string id)
        {
            var userId = await _userService.GetUserIdAsync();
            var folderKey = string.Format(Keys_FoldersFormat, userId);
            var folders = await _storageService.GetAsync<Dictionary<string, FolderData>>(folderKey);
            if(folders == null || !folders.ContainsKey(id))
            {
                return;
            }
            folders.Remove(id);
            await _storageService.SaveAsync(folderKey, folders);
            _decryptedFolderCache = null;

            // Items in a deleted folder are re-assigned to "No Folder"
            var ciphers = await _storageService.GetAsync<Dictionary<string, CipherData>>(
                string.Format(Keys_CiphersFormat, userId));
            if(ciphers != null)
            {
                var updates = new List<CipherData>();
                foreach(var c in ciphers)
                {
                    if(c.Value.FolderId == id)
                    {
                        c.Value.FolderId = null;
                        updates.Add(c.Value);
                    }
                }
                if(updates.Any())
                {
                    await _cipherService.UpsertAsync(updates);
                }
            }
        }

        public async Task DeleteWithServerAsync(string id)
        {
            await _apiService.DeleteFolderAsync(id);
            await DeleteAsync(id);
        }

        private class FolderLocaleComparer : IComparer<FolderView>
        {
            private readonly II18nService _i18nService;

            public FolderLocaleComparer(II18nService i18nService)
            {
                _i18nService = i18nService;
            }

            public int Compare(FolderView a, FolderView b)
            {
                var aName = a.Name;
                var bName = b.Name;
                if(aName == null && bName != null)
                {
                    return -1;
                }
                if(aName != null && bName == null)
                {
                    return 1;
                }
                if(aName == null && bName == null)
                {
                    return 0;
                }
                return _i18nService.StringComparer.Compare(aName, bName);
            }
        }
    }
}
