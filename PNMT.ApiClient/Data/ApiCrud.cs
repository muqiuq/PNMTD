﻿using PNMTD.Models.Responses;
using System.Net.Http.Json;

namespace PNMT.ApiClient.Data
{
    public class ApiCrud<T>
    {
        protected readonly HttpClient httpClient;
        private readonly string path;

        public ApiCrud(HttpClient httpClient, string path)
        {
            this.httpClient = httpClient;
            this.path = path;
        }

        public async Task<List<T>> GetAll()
        {
            return await httpClient.GetFromJsonAsync<List<T>>($"/{path}").ConfigureAwait(false);
        }

        public async Task Delete(Guid Id)
        {
            var result = await httpClient.DeleteFromJsonAsync<DefaultResponse>($"/{path}/{Id}").ConfigureAwait(false);

            if (!result.Success)
            {
                throw new PNMTDApiException($"Delete Error");
            }
        }

        public async Task<T> Get(Guid Id)
        {
            return await httpClient.GetFromJsonAsync<T>($"/{path}/{Id}").ConfigureAwait(false);
        }

        public async Task AddNew(T t)
        {
            var result = await httpClient.PostAsJsonAsync<T>($"/{path}", t).ConfigureAwait(false);
            var response = await result.Content.ReadFromJsonAsync<DefaultResponse>();

            if (!result.IsSuccessStatusCode || !response.Success)
            {
                throw new PNMTDApiException($"HTTP {result.StatusCode}");
            }
        }

        public async Task Update(T t)
        {
            var result = await httpClient.PutAsJsonAsync<T>($"/{path}", t).ConfigureAwait(false);
            
            var response = await result.Content.ReadFromJsonAsync<DefaultResponse>();

            if (!result.IsSuccessStatusCode || !response.Success)
            {
                throw new PNMTDApiException($"HTTP {result.StatusCode}");
            }
        }

    }
}