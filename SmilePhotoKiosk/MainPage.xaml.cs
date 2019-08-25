using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;

using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Automation.Peers;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Capture;
using Windows.Media.FaceAnalysis;
using Windows.Media.MediaProperties;
using Windows.System.Threading;
using Windows.UI.Xaml.Shapes;
using Windows.Storage;
using Windows.Storage.Streams;

using Microsoft.Azure.CognitiveServices.Vision.Face;
using Microsoft.Azure.CognitiveServices.Vision.Face.Models;
using LocalDetectedFace = Windows.Media.FaceAnalysis.DetectedFace;
using RemoteDetectedFace = Microsoft.Azure.CognitiveServices.Vision.Face.Models.DetectedFace;
using Windows.Media.Core;
using Windows.Media.Capture.Frames;

namespace SmilePhotoKiosk
{
   public enum NotifyType
   {
      StatusMessage,
      ErrorMessage
   };

   [ComImport]
   [Guid("5b0d3235-4dba-4d44-865e-8f1d0e4fd04d")]
   [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
   unsafe interface IMemoryBufferByteAccess
   {
      void GetBuffer(out byte* buffer, out uint capacity);
   }

   /// <summary>
   /// An empty page that can be used on its own or navigated to within a Frame.
   /// </summary>
   public sealed partial class MainPage : Page
   {
      /// <summary>
      /// Brush for drawing the bounding box around each identified face.
      /// </summary>
      private readonly SolidColorBrush lineBrush = new SolidColorBrush(Windows.UI.Colors.Yellow);

      /// <summary>
      /// Thickness of the face bounding box lines.
      /// </summary>
      private readonly double lineThickness = 2.0;

      /// <summary>
      /// Transparent fill for the bounding box.
      /// </summary>
      private readonly SolidColorBrush fillBrush = new SolidColorBrush(Windows.UI.Colors.Transparent);

      /// <summary>
      /// References a MediaCapture instance; is null when not in Streaming state.
      /// </summary>
      private MediaCapture mediaCapture;
      private MediaFrameReader mediaFrameReader;

      /// <summary>
      /// Cache of properties from the current MediaCapture device which is used for capturing the preview frame.
      /// </summary>
      private VideoEncodingProperties videoProperties;

      /// <summary>
      /// References a FaceTracker instance.
      /// </summary>
      private FaceTracker faceTracker;

      /// <summary>
      /// Semaphore to ensure FaceTracking logic only executes one at a time
      /// </summary>
      private SemaphoreSlim frameProcessingSemaphore = new SemaphoreSlim(1);

      private IFaceClient faceClient;

      // Folder in which the captures will be stored (initialized in InitializeCameraAsync)
      private StorageFolder captureFolder = null;

      private readonly double smileThreshold = 0.95;

      /// <summary>
      /// Initializes a new instance of the <see cref="TrackFacesInWebcam"/> class.
      /// </summary>
      public MainPage()
      {
         this.InitializeComponent();
         App.Current.Suspending += this.OnSuspending;
      }

      /// <summary>
      /// Responds when we navigate to this page.
      /// </summary>
      /// <param name="e">Event data</param>
      protected override async void OnNavigatedTo(NavigationEventArgs e)
      {
         if (ApplicationData.Current.LocalSettings.Values.ContainsKey("ApiKey"))
         {
            ApiKey.Text = ApplicationData.Current.LocalSettings.Values["ApiKey"].ToString();
         }
         if (ApplicationData.Current.LocalSettings.Values.ContainsKey("ApiEndPoint"))
         {
            ApiEndPoint.Text = ApplicationData.Current.LocalSettings.Values["ApiEndPoint"].ToString();
         }

         // The 'await' operation can only be used from within an async method but class constructors
         // cannot be labeled as async, and so we'll initialize FaceTracker here.
         if (this.faceTracker == null)
         {
            this.faceTracker = await FaceTracker.CreateAsync();
         }

         this.faceClient = new FaceClient(
             new ApiKeyServiceClientCredentials(ApiKey.Text),
             new System.Net.Http.DelegatingHandler[] { })
         {
            Endpoint = ApiEndPoint.Text
         };

         var picturesLibrary = await StorageLibrary.GetLibraryAsync(KnownLibraryId.Pictures);
         // Fall back to the local app storage if the Pictures Library is not available
         captureFolder = picturesLibrary.SaveFolder ?? ApplicationData.Current.LocalFolder;

         await this.StartWebcamStreaming();
         //await this.CreateFaceDetectionEffectAsync();
      }

      /// <summary>
      /// Adds face detection to the preview stream, registers for its events, enables it, and gets the FaceDetectionEffect instance
      /// </summary>
      /// <returns></returns>
      private async Task CreateFaceDetectionEffectAsync()
      {
         // Create the definition, which will contain some initialization settings
         var definition = new FaceDetectionEffectDefinition();

         // To ensure preview smoothness, do not delay incoming samples
         definition.SynchronousDetectionEnabled = false;

         // In this scenario, choose detection speed over accuracy
         definition.DetectionMode = FaceDetectionMode.HighPerformance;

         // Add the effect to the preview stream
         var _faceDetectionEffect = (FaceDetectionEffect)await this.mediaCapture.AddVideoEffectAsync(definition, MediaStreamType.VideoPreview);

         // Register for face detection events
         _faceDetectionEffect.FaceDetected += FaceDetectionEffect_FaceDetected;

         // Choose the shortest interval between detection events
         _faceDetectionEffect.DesiredDetectionInterval = TimeSpan.FromMilliseconds(100);

         // Start detecting faces
         _faceDetectionEffect.Enabled = true;
      }

      private async void FaceDetectionEffect_FaceDetected(FaceDetectionEffect sender, FaceDetectedEventArgs args)
      {
         // Ask the UI thread to render the face bounding boxes
         //await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => HighlightDetectedFaces(args.ResultFrame.DetectedFaces));
         //var previewFrameSize = new Windows.Foundation.Size(previewFrame.SoftwareBitmap.PixelWidth, previewFrame.SoftwareBitmap.PixelHeight);
         //var ignored = this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
         //{
         //   //this.SetupVisualization(previewFrameSize, faces);
         //   this.SetupProgressBar(anger, contempt, disgust, fear, happiness, sadness, surprise, smile);
         //});
         var x = args.ResultFrame.ExtendedProperties;
         var detectedFaces = args.ResultFrame.DetectedFaces;
         await this.ProcessCurrentVideoFrameAsync(detectedFaces);
      }

      /// <summary>
      /// Responds to App Suspend event to stop/release MediaCapture object if it's running and return to Idle state.
      /// </summary>
      /// <param name="sender">The source of the Suspending event</param>
      /// <param name="e">Event data</param>
      private void OnSuspending(object sender, Windows.ApplicationModel.SuspendingEventArgs e)
      {
         ApplicationData.Current.LocalSettings.Values["ApiKey"] = ApiKey.Text;
         ApplicationData.Current.LocalSettings.Values["ApiEndPoint"] = ApiEndPoint.Text;

         var deferral = e.SuspendingOperation.GetDeferral();
         try
         {
            this.ShutdownWebCam();
            this.VisualizationCanvas.Children.Clear();
            this.faceClient.Dispose();
            this.faceClient = null;
         }
         finally
         {
            deferral.Complete();
         }
      }

      /// <summary>
      /// Initializes a new MediaCapture instance and starts the Preview streaming to the CamPreview UI element.
      /// </summary>
      /// <returns>Async Task object returning true if initialization and streaming were successful and false if an exception occurred.</returns>
      private async Task<bool> StartWebcamStreaming()
      {
         bool successful = true;

         try
         {
            this.mediaCapture = new MediaCapture();

            var frameSourceGroups = await MediaFrameSourceGroup.FindAllAsync();

            MediaFrameSourceGroup selectedGroup = null;
            MediaFrameSourceInfo colorSourceInfo = null;

            foreach (var sourceGroup in frameSourceGroups)
            {
               foreach (var sourceInfo in sourceGroup.SourceInfos)
               {
                  if ((sourceInfo.MediaStreamType == MediaStreamType.VideoPreview || sourceInfo.MediaStreamType == MediaStreamType.VideoRecord)
                      && sourceInfo.SourceKind == MediaFrameSourceKind.Color)
                  {
                     colorSourceInfo = sourceInfo;
                     break;
                  }
               }
               if (colorSourceInfo != null)
               {
                  selectedGroup = sourceGroup;
                  break;
               }
            }

            if (colorSourceInfo != null)
            {
               var settings = new MediaCaptureInitializationSettings()
               {
                  SourceGroup = selectedGroup,
                  SharingMode = MediaCaptureSharingMode.ExclusiveControl,
                  MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                  StreamingCaptureMode = StreamingCaptureMode.Video
               };

               await mediaCapture.InitializeAsync(settings);

               this.mediaCapture.Failed += this.MediaCapture_CameraStreamFailed;
               var colorFrameSource = mediaCapture.FrameSources[colorSourceInfo.Id];
               var preferredFormat = colorFrameSource.SupportedFormats
                   .OrderByDescending(x => x.VideoFormat.Width)
                   .FirstOrDefault(x => x.VideoFormat.Width <= 1920 && x.Subtype.Equals(MediaEncodingSubtypes.Nv12, StringComparison.OrdinalIgnoreCase));

               await colorFrameSource.SetFormatAsync(preferredFormat);
               this.mediaFrameReader = await this.mediaCapture.CreateFrameReaderAsync(colorFrameSource);
               this.mediaFrameReader.FrameArrived += MediaFrameReader_FrameArrived;
               await this.mediaFrameReader.StartAsync();
            }

            // Cache the media properties as we'll need them later.
            var deviceController = this.mediaCapture.VideoDeviceController;
            this.videoProperties = deviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview) as VideoEncodingProperties;

            // Immediately start streaming to our CaptureElement UI.
            // NOTE: CaptureElement's Source must be set before streaming is started.
            this.CamPreview.Source = this.mediaCapture;
            await this.mediaCapture.StartPreviewAsync();
         }
         catch (System.UnauthorizedAccessException)
         {
            // If the user has disabled their webcam this exception is thrown; provide a descriptive message to inform the user of this fact.
            NotifyUser("Webcam is disabled or access to the webcam is disabled for this app.\nEnsure Privacy Settings allow webcam usage.", NotifyType.ErrorMessage);
            successful = false;
         }
         catch (Exception ex)
         {
            NotifyUser(ex.ToString(), NotifyType.ErrorMessage);
            successful = false;
         }

         return successful;
      }

      private void MediaFrameReader_FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
      {
      }

      /// <summary>
      /// Safely stops webcam streaming (if running) and releases MediaCapture object.
      /// </summary>
      private async void ShutdownWebCam()
      {
         if (this.mediaCapture != null)
         {
            if (this.mediaCapture.CameraStreamState == Windows.Media.Devices.CameraStreamState.Streaming)
            {
               try
               {
                  await this.mediaCapture.StopPreviewAsync();
               }
               catch (Exception)
               {
                  ;   // Since we're going to destroy the MediaCapture object there's nothing to do here
               }
            }
            this.mediaCapture.Dispose();
         }

         this.CamPreview.Source = null;
         this.mediaCapture = null;
      }

      /// <summary>
      /// This method is invoked by a ThreadPoolTimer to execute the FaceTracker and Visualization logic at approximately 15 frames per second.
      /// </summary>
      /// <remarks>
      /// Keep in mind this method is called from a Timer and not synchronized with the camera stream. Also, the processing time of FaceTracker
      /// will vary depending on the size of each frame and the number of faces being tracked. That is, a large image with several tracked faces may
      /// take longer to process.
      /// </remarks>
      /// <param name="timer">Timer object invoking this call</param>
      private async Task ProcessCurrentVideoFrameAsync(IReadOnlyList<LocalDetectedFace> localFaces)
      {
         // If a lock is being held it means we're still waiting for processing work on the previous frame to complete.
         // In this situation, don't wait on the semaphore but exit immediately.
         if (!frameProcessingSemaphore.Wait(0))
         {
            return;
         }

         try
         {
            // Create a VideoFrame object specifying the pixel format we want our capture image to be (NV12 bitmap in this case).
            // GetPreviewFrame will convert the native webcam frame into this format.
            const BitmapPixelFormat InputPixelFormat = BitmapPixelFormat.Rgba8;
            using (VideoFrame previewFrame = new VideoFrame(InputPixelFormat, (int)this.videoProperties.Width, (int)this.videoProperties.Height))
            {
               await this.mediaCapture.GetPreviewFrameAsync(previewFrame);

               if (localFaces.Count > 0)
               {
                  IList<FaceAttributeType> faceAttributes =
                      new FaceAttributeType[]
                      {
                         FaceAttributeType.Smile, FaceAttributeType.Emotion
                      };
                  using (var captureStream = new InMemoryRandomAccessStream())
                  {
                     await mediaCapture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), captureStream);
                     captureStream.Seek(0);
                     IList<RemoteDetectedFace> remoteFaces =
                             await faceClient.Face.DetectWithStreamAsync(captureStream.AsStreamForRead(), true, true, faceAttributes);
                     if (remoteFaces.Count > 0)
                     {
                        var remoteFace = remoteFaces[0];

                        // Create our visualization using the frame dimensions and face results but run it on the UI thread.
                        var previewFrameSize = new Windows.Foundation.Size(previewFrame.SoftwareBitmap.PixelWidth, previewFrame.SoftwareBitmap.PixelHeight);
                        var ignored = this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                        {
                           this.SetupVisualization(previewFrameSize, localFaces);
                           this.SetupProgressBar(
                              remoteFace.FaceAttributes.Emotion.Anger,
                              remoteFace.FaceAttributes.Emotion.Contempt,
                              remoteFace.FaceAttributes.Emotion.Disgust,
                              remoteFace.FaceAttributes.Emotion.Fear,
                              remoteFace.FaceAttributes.Emotion.Happiness,
                              remoteFace.FaceAttributes.Emotion.Sadness,
                              remoteFace.FaceAttributes.Emotion.Surprise,
                              remoteFace.FaceAttributes.Smile.Value);
                        });

                        if (remoteFace.FaceAttributes.Smile.Value > smileThreshold)
                        {
                           var file = await captureFolder.CreateFileAsync("SmileFace.jpg", CreationCollisionOption.GenerateUniqueName);
                           var croppedBitmap = CropImageByRect(previewFrame.SoftwareBitmap, remoteFace.FaceRectangle);
                           await SaveSoftwareBitmapAsync(croppedBitmap, file);
                        }
                     }
                  }
               }
            }
         }
         catch (Exception ex)
         {
            var ignored = this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
               NotifyUser(ex.ToString(), NotifyType.ErrorMessage);
            });
         }
         finally
         {
            frameProcessingSemaphore.Release();
         }

      }

      private void SetupProgressBar(double anger, double contempt, double disgust, double fear, double happiness, double sadness, double surprise, double smile)
      {
         progress_anger.Value = anger * progress_anger.Maximum;
         progress_contempt.Value = contempt * progress_contempt.Maximum;
         progress_disgust.Value = disgust * progress_disgust.Maximum;
         progress_fear.Value = fear * progress_fear.Maximum;
         progress_happiness.Value = happiness * progress_happiness.Maximum;
         progress_sadness.Value = sadness * progress_sadness.Maximum;
         progress_surprise.Value = surprise * progress_surprise.Maximum;
         progress_smile.Value = smile * progress_smile.Maximum;
      }

      /// <summary>
      /// Takes the webcam image and FaceTracker results and assembles the visualization onto the Canvas.
      /// </summary>
      /// <param name="framePixelSize">Width and height (in pixels) of the video capture frame</param>
      /// <param name="foundFaces">List of detected faces; output from FaceTracker</param>
      private void SetupVisualization(Windows.Foundation.Size framePixelSize, IReadOnlyList<LocalDetectedFace> foundFaces)
      {
         this.VisualizationCanvas.Children.Clear();

         double actualWidth = this.VisualizationCanvas.ActualWidth;
         double actualHeight = this.VisualizationCanvas.ActualHeight;

         if (foundFaces != null && actualWidth != 0 && actualHeight != 0)
         {
            double widthScale = framePixelSize.Width / actualWidth;
            double heightScale = framePixelSize.Height / actualHeight;

            var boxes = from face in foundFaces
                        select new Rectangle()
                        {
                           Width = (uint)(face.FaceBox.Width / widthScale),
                           Height = (uint)(face.FaceBox.Height / heightScale),
                           Fill = this.fillBrush,
                           Stroke = this.lineBrush,
                           StrokeThickness = this.lineThickness,
                           Margin = new Thickness((uint)(face.FaceBox.X / widthScale), (uint)(face.FaceBox.Y / heightScale), 0, 0)
                        };

            foreach (var box in boxes)
            {
               this.VisualizationCanvas.Children.Add(box);
            }
         }
      }

      /// <summary>
      /// Handles MediaCapture stream failures by shutting down streaming and returning to Idle state.
      /// </summary>
      /// <param name="sender">The source of the event, i.e. our MediaCapture object</param>
      /// <param name="args">Event data</param>
      private void MediaCapture_CameraStreamFailed(MediaCapture sender, object args)
      {
         // MediaCapture is not Agile and so we cannot invoke its methods on this caller's thread
         // and instead need to schedule the state change on the UI thread.
         var ignored = this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
         {
         });
      }

      /// <summary>
      /// Display a message to the user.
      /// This method may be called from any thread.
      /// </summary>
      /// <param name="strMessage"></param>
      /// <param name="type"></param>
      private void NotifyUser(string strMessage, NotifyType type)
      {
         // If called from the UI thread, then update immediately.
         // Otherwise, schedule a task on the UI thread to perform the update.
         if (Dispatcher.HasThreadAccess)
         {
            UpdateStatus(strMessage, type);
         }
         else
         {
            var task = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => UpdateStatus(strMessage, type));
         }
      }

      private void UpdateStatus(string strMessage, NotifyType type)
      {
         switch (type)
         {
            case NotifyType.StatusMessage:
               StatusBorder.Background = new SolidColorBrush(Windows.UI.Colors.Green);
               break;
            case NotifyType.ErrorMessage:
               StatusBorder.Background = new SolidColorBrush(Windows.UI.Colors.Red);
               break;
         }

         StatusBlock.Text = strMessage;

         // Collapse the StatusBlock if it has no text to conserve real estate.
         StatusBorder.Visibility = (StatusBlock.Text != String.Empty) ? Visibility.Visible : Visibility.Collapsed;
         if (StatusBlock.Text != String.Empty)
         {
            StatusBorder.Visibility = Visibility.Visible;
            StatusPanel.Visibility = Visibility.Visible;
         }
         else
         {
            StatusBorder.Visibility = Visibility.Collapsed;
            StatusPanel.Visibility = Visibility.Collapsed;
         }

         // Raise an event if necessary to enable a screen reader to announce the status update.
         var peer = FrameworkElementAutomationPeer.FromElement(StatusBlock);
         if (peer != null)
         {
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
         }
      }

      /// <summary>
      /// Saves a SoftwareBitmap to the specified StorageFile
      /// </summary>
      /// <param name="bitmap">SoftwareBitmap to save</param>
      /// <param name="file">Target StorageFile to save to</param>
      /// <returns></returns>
      private static async Task SaveSoftwareBitmapAsync(SoftwareBitmap bitmap, StorageFile file)
      {
         using (var outputStream = await file.OpenAsync(FileAccessMode.ReadWrite))
         {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outputStream);

            var propertySet = new Windows.Graphics.Imaging.BitmapPropertySet();
            var orientationValue = new Windows.Graphics.Imaging.BitmapTypedValue(
                1, // Defined as EXIF orientation = "normal"
                Windows.Foundation.PropertyType.UInt16
                );
            propertySet.Add("System.Photo.Orientation", orientationValue);

            await encoder.BitmapProperties.SetPropertiesAsync(propertySet);

            // Grab the data from the SoftwareBitmap
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
         }
      }

      private unsafe SoftwareBitmap CropImageByRect(SoftwareBitmap inputBitmap, FaceRectangle rect)
      {
         var outputBitmap = new SoftwareBitmap(inputBitmap.BitmapPixelFormat, rect.Width, rect.Height, inputBitmap.BitmapAlphaMode);

         using (BitmapBuffer inputBuffer = inputBitmap.LockBuffer(BitmapBufferAccessMode.Read))
         {
            using (BitmapBuffer outputBuffer = outputBitmap.LockBuffer(BitmapBufferAccessMode.Write))
            {
               using (var inputReference = inputBuffer.CreateReference())
               {
                  byte* dataInBytes;
                  uint dataInCapacity;
                  ((IMemoryBufferByteAccess)inputReference).GetBuffer(out dataInBytes, out dataInCapacity);
                  BitmapPlaneDescription inputBufferLayout = inputBuffer.GetPlaneDescription(0);

                  using (var outputReference = outputBuffer.CreateReference())
                  {
                     byte* dataOutBytes;
                     uint dataOutCapacity;
                     ((IMemoryBufferByteAccess)outputReference).GetBuffer(out dataOutBytes, out dataOutCapacity);

                     // Fill-in the BGRA plane
                     BitmapPlaneDescription outputBufferLayout = outputBuffer.GetPlaneDescription(0);
                     for (int i = 0; i < outputBufferLayout.Height; i++)
                     {
                        for (int j = 0; j < outputBufferLayout.Width; j++)
                        {
                           dataOutBytes[outputBufferLayout.StartIndex + outputBufferLayout.Stride * i + 4 * j + 0] =
                              dataInBytes[inputBufferLayout.StartIndex + inputBufferLayout.Stride * (rect.Top + i) + 4 * (rect.Left + j) + 0];
                           dataOutBytes[outputBufferLayout.StartIndex + outputBufferLayout.Stride * i + 4 * j + 1] =
                              dataInBytes[inputBufferLayout.StartIndex + inputBufferLayout.Stride * (rect.Top + i) + 4 * (rect.Left + j) + 1];
                           dataOutBytes[outputBufferLayout.StartIndex + outputBufferLayout.Stride * i + 4 * j + 2] =
                              dataInBytes[inputBufferLayout.StartIndex + inputBufferLayout.Stride * (rect.Top + i) + 4 * (rect.Left + j) + 2];
                           dataOutBytes[outputBufferLayout.StartIndex + outputBufferLayout.Stride * i + 4 * j + 3] =
                              dataInBytes[inputBufferLayout.StartIndex + inputBufferLayout.Stride * (rect.Top + i) + 4 * (rect.Left + j) + 3];
                        }
                     }
                  }
               }
            }
         }

         return outputBitmap;
      }

   }
}
