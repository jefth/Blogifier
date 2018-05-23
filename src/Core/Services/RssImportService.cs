﻿using Core.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.SyndicationFeed;
using Microsoft.SyndicationFeed.Rss;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace Core.Services
{
    public interface IRssImportService
    {
        Task ImportFromFile(IFormFile file);
    }

    public class RssImportService : IRssImportService
    {
        IUnitOfWork _db;
        IStorageService _storage;
        IHttpContextAccessor _http;
        AppUser _user;
        string _siteUrl;

        public RssImportService(IUnitOfWork db, IHttpContextAccessor http, IStorageService storage)
        {
            _db = db;
            _user = _db.Authors.Single(a => a.UserName == http.HttpContext.User.Identity.Name);
            _storage = storage;
            _http = http;
        }

        public async Task ImportFromFile(IFormFile file)
        {
            var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);

            var req = _http.HttpContext.Request;
            var xxx = req.PathBase;
            
            using (var xmlReader = XmlReader.Create(reader, new XmlReaderSettings() { }))
            {
                var feedReader = new RssFeedReader(xmlReader);

                while (await feedReader.Read())
                {
                    if(feedReader.ElementType == SyndicationElementType.Link)
                    {
                        var link = await feedReader.ReadLink();
                        _siteUrl = link.Uri.ToString();
                    }

                    if (feedReader.ElementType == SyndicationElementType.Item)
                    {
                        try
                        {
                            var item = await feedReader.ReadItem();

                            PostItem post = new PostItem
                            {
                                Author = await _db.Authors.GetItem(a => a.UserName == _user.UserName),
                                Title = item.Title,
                                Description = item.Title,
                                Content = item.Description,
                                Slug = await GetSlug(item.Title),
                                Published = item.Published.DateTime,
                                Status = SaveStatus.Publishing
                            };

                            await ImportImages(post);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine(ex.Message);
                        }
                    }
                }
            }
        }

        async Task ImportImages(PostItem post)
        {
            var images = GetImages(post.Content);
            await SaveAssets(images, post, false);

            var converter = new ReverseMarkdown.Converter();
            post.Content = converter.Convert(post.Content);

            await _db.BlogPosts.SaveItem(post);
        }

        IList<string> GetImages(string html)
        {
            var links = new List<string>();
            string rgx = @"<img[^>]*?src\s*=\s*[""']?([^'"" >]+?)[ '""][^>]*?>";

            if (string.IsNullOrEmpty(html))
                return links;

            MatchCollection matchesImgSrc = Regex.Matches(html, rgx, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (matchesImgSrc != null)
            {
                foreach (Match m in matchesImgSrc)
                {
                    links.Add(m.Groups[1].Value);
                }
            }
            return links;
        }

        IList<string> GetAttachements(string html)
        {
            var links = new List<string>();
            string rgx = "<(a|link).*?href=(\"|')(.+?)(\"|').*?>";

            if (string.IsNullOrEmpty(html))
                return links;

            MatchCollection matches = Regex.Matches(html, rgx, RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (matches != null)
            {
                foreach (Match m in matches)
                {
                    try
                    {
                        var link = m.Value.Replace("\">", "\"/>").ToLower();
                        var href = XElement.Parse(link).Attribute("href").Value;
                        links.Add(href);
                    }
                    catch { }
                }
            }
            return links;
        }

        async Task SaveAssets(IList<string> assets, PostItem post, bool isAttachement)
        {
            if (assets.Any())
            {
                foreach (var item in assets)
                {
                    var uri = "";
                    var webRoot = "/";
                    try
                    {
                        uri = ValidateUrl(item);

                        var path = string.Format("{0}/{1}", post.Published.Year, post.Published.Month);

                        Asset asset;
                        if (uri.Contains("data:image"))
                        {
                            asset = await _storage.UploadBase64Image(uri, webRoot, path);
                        }
                        else
                        {
                            asset = await _storage.UploadFromWeb(new Uri(uri), webRoot, path);
                        }

                        asset.UserId = _user.Id;
                        asset.Published = SystemClock.Now();

                        if (isAttachement)
                            asset.AssetType = AssetType.Attachment;

                        post.Content = post.Content.ReplaceIgnoreCase(uri.ToString(), asset.Url);

                        _db.Assets.Add(asset);
                        _db.Complete();
                    }
                    catch (Exception)
                    {
                        //_logger.LogError(string.Format("Error importing [{0}] : {1}", item, ex.Message));
                    }
                }
            }
        }

        async Task<string> GetSlug(string title)
        {
            string slug = title.ToSlug();
            BlogPost post;

            post = _db.BlogPosts.Single(p => p.Slug == slug);

            if (post == null)
                return await Task.FromResult(slug);

            for (int i = 2; i < 100; i++)
            {
                post = _db.BlogPosts.Single(p => p.Slug == $"{slug}{i}");

                if (post == null)
                {
                    return await Task.FromResult(slug + i.ToString());
                }
            }

            return await Task.FromResult(slug);
        }

        string ValidateUrl(string link)
        {
            var url = link;
            var domain = "";
            if (url.StartsWith("~"))
            {
                url = url.Replace("~", domain);
            }
            if (url.StartsWith("/"))
            {
                url = string.Concat(domain, url);
            }
            return url;
        }
    }
}