using Microsoft.WindowsAzure.MediaServices.Client;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace ThinkVideo.StandardEncoding
{
    class Program
    {
        // Read values from the App.config file.
        private static readonly string _mediaServicesAccountName = ConfigurationManager.AppSettings["MediaServicesAccountName"];
        private static readonly string _mediaServicesAccountKey = ConfigurationManager.AppSettings["MediaServicesAccountKey"];

        private static CloudStorageAccount storageAccount;
        private static CloudBlobClient blobClient;

        // Field for service context.
        private static CloudMediaContext _context = null;
        private static MediaServicesCredentials _cachedCredentials = null;

        static void Main(string[] args)
        {
            try
            {
                // Create and cache the Media Services credentials in a static class variable.
                _cachedCredentials = new MediaServicesCredentials(_mediaServicesAccountName,
                                _mediaServicesAccountKey);

                // Used the chached credentials to create CloudMediaContext.
                _context = new CloudMediaContext(_cachedCredentials);

                storageAccount = CloudStorageAccount.Parse(ConfigurationManager.AppSettings["AzureStorageConnectionString"]);
                blobClient = storageAccount.CreateCloudBlobClient();

                foreach (var ICloudBlob in ListContainerContents())
                {
                    IAsset inputAsset = CreateAssetBlob(ICloudBlob);
                    Console.WriteLine("Encode to adaptive bitraite MP4s and get on demand URLs.\n");
                    IAsset encodedAsset = EncodeToAdaptiveBitrateMP4s(inputAsset, AssetCreationOptions.None);

                    PublishAssetGetURLs(encodedAsset);
                }
            }
            catch (Exception exception)
            {
                // Parse the XML error message in the Media Services response and create a new
                // exception with its content.
                exception = MediaServicesExceptionParser.Parse(exception);

                Console.Error.WriteLine(exception.Message);
            }
            finally
            {
                Console.ReadLine();
            }
        }

        static public List<ICloudBlob> ListContainerContents()
        {
            var results = new List<ICloudBlob>();

            // Retrieve reference to a previously created container.
            CloudBlobContainer container = blobClient.GetContainerReference("newvideos");

            // Loop over items within the container and output the length and URI.
            foreach (ICloudBlob item in container.ListBlobs(null, false))
            {
                if (item.GetType() == typeof(CloudBlockBlob))
                {
                    results.Add(item);
                }
            }

            return results;
        }

        /// <summary>
        /// Creates a new asset and copies blobs from the specifed storage account.
        /// </summary>
        /// <param name="mediaBlobContainer">The specified blob container.</param>
        /// <returns>The new asset.</returns>
        static public IAsset CreateAssetBlob(ICloudBlob blob)
        {
            // Create a new asset. 
            IAsset asset = _context.Assets.Create("NewAsset_Test", AssetCreationOptions.None);

            IAccessPolicy writePolicy = _context.AccessPolicies.Create("writePolicy",
                TimeSpan.FromHours(24), AccessPermissions.Write);

            ILocator destinationLocator = _context.Locators.CreateLocator(LocatorType.Sas, asset, writePolicy);

            var assetFile = asset.AssetFiles.Create(blob.Name);
            assetFile.ContentFileSize = blob.Properties.Length;
            assetFile.Update();

            asset.Update();

            destinationLocator.Delete();
            writePolicy.Delete();

            return asset;
        }

        static public IAsset UploadFile(string fileName, AssetCreationOptions options)
        {
            IAsset inputAsset = _context.Assets.CreateFromFile(
                fileName,
                options,
                (af, p) =>
                {
                    Console.WriteLine("Uploading '{0}' - Progress: {1:0.##}%", af.Name, p.Progress);
                });

            Console.WriteLine("Asset {0} created.", inputAsset.Id);

            return inputAsset;
        }

        static public IAsset EncodeToAdaptiveBitrateMP4s(IAsset asset, AssetCreationOptions options)
        {
            // Prepare a job with a single task to transcode the specified asset
            // into a multi-bitrate asset.

            /*
            IMediaProcessor processor = _context.MediaProcessors
                .Where(p => p.Name == "Media Encoder Standard")
                .ToList()
                .OrderBy(p => new Version(p.Version))
                .LastOrDefault();
                */

            IJob job = _context.Jobs.CreateWithSingleTask(
                "Media Encoder Standard",
                "H264 Multiple Bitrate 720p",
                asset,
                "Adaptive Bitrate MP4",
                options);

            Console.WriteLine("Submitting transcoding job...");

            // Submit the job and wait until it is completed.
            job.Submit();

            job = job.StartExecutionProgressTask(
                j =>
                {
                    Console.WriteLine("Job state: {0}", j.State);
                    Console.WriteLine("Job progress: {0:0.##}%", j.GetOverallProgress());
                },
                CancellationToken.None).Result;

            Console.WriteLine("Transcoding job finished.");

            IAsset outputAsset = job.OutputMediaAssets[0];

            return outputAsset;
        }

        static public void PublishAssetGetURLs(IAsset asset, bool onDemaindURL = true, string fileExt = "")
        {
            // Publish the output asset by creating an Origin locator for adaptive streaming,
            // and a SAS locator for progressive download.
            if (onDemaindURL)
            {
                _context.Locators.Create(
                    LocatorType.OnDemandOrigin,
                    asset,
                    AccessPermissions.Read,
                    TimeSpan.FromDays(30));

                // Get the Smooth Streaming, HLS and MPEG-DASH URLs for adaptive streaming,
                // and the Progressive Download URL.
                Uri smoothStreamingUri = asset.GetSmoothStreamingUri();
                Uri hlsUri = asset.GetHlsUri();
                Uri mpegDashUri = asset.GetMpegDashUri();

                // Display  the streaming URLs.
                Console.WriteLine("Use the following URLs for adaptive streaming: ");
                Console.WriteLine(smoothStreamingUri);
                Console.WriteLine(hlsUri);
                Console.WriteLine(mpegDashUri);
                Console.WriteLine();
            }
            else
            {
                _context.Locators.Create(
                    LocatorType.Sas,
                    asset,
                    AccessPermissions.Read,
                    TimeSpan.FromDays(30));

                IEnumerable<IAssetFile> assetFiles = asset
                    .AssetFiles
                    .ToList()
                    .Where(af => af.Name.EndsWith(fileExt, StringComparison.OrdinalIgnoreCase));

                // Get the URls for progressive download for each specified file that was generated as a result
                // of encoding.
                List<Uri> sasUris = assetFiles.Select(af => af.GetSasUri()).ToList();

                // Display the URLs for progressive download.
                Console.WriteLine("Use the following URLs for progressive download.");
                sasUris.ForEach(uri => Console.WriteLine(uri + "\n"));
                Console.WriteLine();
            }
        }
    }
}
