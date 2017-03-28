﻿using AdaptiveCards;
using AdaptiveCards.Renderers;
using AdaptiveCards.Renderers.Threading.Tasks.Schedulers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdaptiveCards.Renderers
{
    /// <summary>
    /// Renderer which renders adaptive card to an image
    /// </summary>
    public class ImageRenderer
    {
        private XamlRenderer _xamlRenderer;
        private string _stylePath;

        /// <summary>
        /// You can use this from within a WPF app, passing in resource dictionary directly
        /// </summary>
        /// <param name="options"></param>
        /// <param name="resources"></param>
        public ImageRenderer(RenderOptions options, ResourceDictionary resources)
        {
            Options = options;
            Options.SupportInteraction = false;
            _xamlRenderer = new XamlRenderer(options, resources);
        }

        /// <summary>
        /// Use this from a Server, passing in the path to a resource dictionary so that UI resources can be created in correct thread
        /// </summary>
        /// <param name="options"></param>
        /// <param name="stylePath"></param>
        public ImageRenderer(RenderOptions options, string stylePath)
        {
            Options = options;
            Options.SupportInteraction = false;
            this._stylePath = stylePath;
        }

        public RenderOptions Options { get; set; }

        /// <summary>
        /// render the card to an png image stream (must be called on STA thread)
        /// </summary>
        /// <param name="context"></param>
        /// <param name="width"></param>
        /// <returns></returns>
        public Stream RenderAdaptiveCard(AdaptiveCard card, int width)
        {
            return _renderAdaptiveCard(_xamlRenderer, card, width);
        }

        /// <summary>
        /// Render the card as a PNG image in a STA compliant way suitable for running on servers 
        /// </summary>
        /// <param name="context"></param>
        /// <param name="width"></param>
        /// <returns>PNG file</returns>
        public async Task<Stream> RenderAdaptiveCardAsync(AdaptiveCard card, int width = 480)
        {
            ImageVisitor visitor = new ImageVisitor();
            
            // prefetch images
            await visitor.GetAllImages(card);

            return await Task.Factory.StartNewSTA(() =>
            {
                var xamlRenderer = new XamlRenderer(Options, this._stylePath, getImageFunc: (url) => visitor.GetCachedImageStream(url));
                return _renderAdaptiveCard(xamlRenderer, card, width);
            }).ConfigureAwait(false);
        }

        protected Stream _renderAdaptiveCard(XamlRenderer renderer, AdaptiveCard card, int width)
        {
            var bitmapImage = _renderToBitmapSource(renderer, card, width);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmapImage));

            MemoryStream stream = new MemoryStream();
            encoder.Save(stream);
            stream.Seek(0, SeekOrigin.Begin);
            return stream;
        }

        /// <summary>
        /// Render card to bitmap source (must be called on STA thread)
        /// </summary>
        /// <param name="context"></param>
        /// <param name="width"></param>
        /// <returns></returns>
        protected BitmapSource _renderToBitmapSource(XamlRenderer renderer, AdaptiveCard card, int width)
        {
            var uiCard = renderer.RenderAdaptiveCard(card);

            uiCard.Measure(new System.Windows.Size(width, 4000));
            uiCard.Arrange(new Rect(new System.Windows.Size(width, uiCard.DesiredSize.Height)));
            uiCard.UpdateLayout();

            RenderTargetBitmap bitmapImage = new RenderTargetBitmap((int)width, (int)uiCard.DesiredSize.Height, 96, 96, PixelFormats.Default);
            bitmapImage.Render(uiCard);
            return bitmapImage;
        }
    }

    /// <summary>
    /// Walk tree and fetch any images referenced
    /// </summary>
    class ImageVisitor : AdaptiveVisitor
    {
        List<Task> tasks = new List<Task>();

        public ImageVisitor()
        {
        }

        public Dictionary<string, MemoryStream> Images = new Dictionary<string, MemoryStream>();

        /// <summary>
        /// Get all images 
        /// </summary>
        /// <param name="card"></param>
        /// <returns></returns>
        public async Task GetAllImages(AdaptiveCard card)
        {
            Visit(card);

            await Task.WhenAll(tasks.ToArray()).ConfigureAwait(false);
        }

        public MemoryStream GetCachedImageStream(String url)
        {
            if (this.Images.TryGetValue(url, out var imageStream))
            {
                imageStream.Position = 0;
                return imageStream;
            }
            return null;
        }

        protected async Task GetImage(string url)
        {
            MemoryStream stream = new MemoryStream();
            using (WebClient client = new WebClient())
            {
                var data = await client.DownloadDataTaskAsync(url).ConfigureAwait(false);
                lock (Images)
                    Images[url] = new MemoryStream(data);
            }
        }

        public override void Visit(AdaptiveCard card)
        {
            if (card.BackgroundImage != null)
                tasks.Add(GetImage(card.BackgroundImage));
            base.Visit(card);
        }

        public override void Visit(Image image)
        {
            tasks.Add(GetImage(image.Url));
            base.Visit(image);
        }
    }


    public static class TaskFactoryExtensions
    {
        private static readonly TaskScheduler _staScheduler = new StaTaskScheduler(numberOfThreads: Environment.ProcessorCount);

        public static Task<TResult> StartNewSTA<TResult>(this TaskFactory factory, Func<TResult> action)
        {
            return factory.StartNew(action, CancellationToken.None, TaskCreationOptions.None, _staScheduler);
        }
    }
}
