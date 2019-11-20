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
using Windows.UI.Xaml.Shapes;
using Windows.Storage;
using Windows.Storage.Streams;

using Microsoft.Azure.CognitiveServices.Vision.Face;
using Microsoft.Azure.CognitiveServices.Vision.Face.Models;
using LocalDetectedFace = Windows.Media.FaceAnalysis.DetectedFace;
using RemoteDetectedFace = Microsoft.Azure.CognitiveServices.Vision.Face.Models.DetectedFace;

using Windows.Media.Capture.Frames;
using Windows.Foundation;

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

   public struct RelativeRectangle
   {
      public double Left;
      public double Top;
      public double Width;
      public double Height;

      public RelativeRectangle(double left, double top, double width, double height)
      {
         Left = left;
         Top = top;
         Width = width;
         Height = height;
      }
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

      private DateTime lastPrintedTime = DateTime.MinValue;

      private Size currentFrameSize = new Size(1, 1);

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

         if (this.faceTracker == null)
         {
            this.faceTracker = await FaceTracker.CreateAsync();
         }

         if (this.faceClient == null)
         {
            this.faceClient = new FaceClient(
                new ApiKeyServiceClientCredentials(ApiKey.Text),
                new System.Net.Http.DelegatingHandler[] { })
            {
               Endpoint = ApiEndPoint.Text
            };
         }

         if (captureFolder == null)
         {
            var picturesLibrary = await StorageLibrary.GetLibraryAsync(KnownLibraryId.Pictures);
            // Fall back to the local app storage if the Pictures Library is not available
            captureFolder = picturesLibrary.SaveFolder ?? ApplicationData.Current.LocalFolder;
         }

         await this.StartWebcamStreaming();
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
            this.CamPreview.FlowDirection = FlowDirection.RightToLeft;
            this.VisualizationCanvas.FlowDirection = FlowDirection.RightToLeft;
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

      private async void MediaFrameReader_FrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
      {
         // If a lock is being held it means we're still waiting for processing work on the previous frame to complete.
         // In this situation, don't wait on the semaphore but exit immediately.
         if (!frameProcessingSemaphore.Wait(0))
         {
            return;
         }
         try
         {
            using (var mediaFrameReference = sender.TryAcquireLatestFrame())
            {
               using (var videoFrame = mediaFrameReference?.VideoMediaFrame?.GetVideoFrame())
               {
                  if (videoFrame != null)
                  {
                     currentFrameSize = new Size(videoFrame.SoftwareBitmap.PixelWidth, videoFrame.SoftwareBitmap.PixelHeight);
                     var localFaces = await FindFacesOnFrameLocalAsync(videoFrame);
                     IList<RemoteDetectedFace> remoteFaces = new List<RemoteDetectedFace>();

                     if (localFaces.Count > 0)
                     {
                        remoteFaces = await FindFacesOnFrameRemoteAsync(videoFrame);

                        var minDelay = TimeSpan.FromSeconds(3);
                        if (remoteFaces.Count > 0 && DateTime.Now.Subtract(lastPrintedTime) > minDelay)
                        {
                           async Task checkForEmotionAsync(Func<RemoteDetectedFace, double> emotionEvaluator, double threshold, string label, SoftwareBitmap bitmap, RemoteDetectedFace face)
                           {
                              var emotionValue = emotionEvaluator(face);
                              if (emotionValue >= threshold)
                              {
                                 var percent = (int)Math.Round(emotionValue * 100.0);
                                 var fileName = $"{percent}% {label}.jpg";
                                 var file = await captureFolder.CreateFileAsync(fileName, CreationCollisionOption.GenerateUniqueName);
                                 var bgraBitmap = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8);
                                 var croppedBitmap = CropImageByRect(bgraBitmap, face.FaceRectangle);
                                 await SaveSoftwareBitmapAsync(croppedBitmap, file);
                                 lastPrintedTime = DateTime.Now;
                              }
                           }

                           List<Func<RemoteDetectedFace, Task>> emotionCheckFuncs = new List<Func<RemoteDetectedFace, Task>>()
                           {
                              (face) => checkForEmotionAsync((f) => f.FaceAttributes.Emotion.Sadness, 0.75, "smutek", videoFrame.SoftwareBitmap, face),
                              (face) => checkForEmotionAsync((f) => f.FaceAttributes.Emotion.Happiness, 0.95, "stesti", videoFrame.SoftwareBitmap, face),
                              (face) => checkForEmotionAsync((f) => f.FaceAttributes.Emotion.Fear, 0.75, "strach", videoFrame.SoftwareBitmap, face),
                              (face) => checkForEmotionAsync((f) => f.FaceAttributes.Emotion.Disgust, 0.75, "znechuceni", videoFrame.SoftwareBitmap, face),
                              (face) => checkForEmotionAsync((f) => f.FaceAttributes.Emotion.Contempt, 0.75, "pohrdani", videoFrame.SoftwareBitmap, face),
                              (face) => checkForEmotionAsync((f) => f.FaceAttributes.Emotion.Anger, 0.75, "vztek", videoFrame.SoftwareBitmap, face),
                              (face) => checkForEmotionAsync((f) => f.FaceAttributes.Emotion.Surprise, 0.85, "prekvapeni", videoFrame.SoftwareBitmap, face)
                           };

                           foreach (var face in remoteFaces)
                           {
                              foreach (var emotionCheckFunc in emotionCheckFuncs)
                              {
                                 await emotionCheckFunc(face);
                              }
                           }
                        }
                     }

                     var updateVisualisationAgainAction = this.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                     {
                        this.UpdateVisualisation(remoteFaces);
                     });
                  }
               }
            }
         }
         catch (Exception ex)
         {
            NotifyUser(ex.ToString(), NotifyType.ErrorMessage);
         }
         finally
         {
            frameProcessingSemaphore.Release();
         }
      }

      private void UpdateVisualisation(IList<RemoteDetectedFace> remoteFaces)
      {
         this.VisualizationCanvas.Children.Clear();

         if (remoteFaces.Count() > 0)
         {
            var relativeRectangles = GetFaceRectanglesRelativeToFrame(remoteFaces, currentFrameSize);
            this.DisplayRelativeRectangles(this.VisualizationCanvas, relativeRectangles);

            string emotionTextItem(RemoteDetectedFace face, string emotIcon, Func<Emotion, double> emotionEvaluator)
            {
               string textualProgressBar(double value)
               {
                  const int segmentCount = 7;
                  const double segmentSize = 1.0 / segmentCount;
                  var segmentsFilled = (int)((value + segmentSize / 2) / segmentSize);
                  var segmentsEmpty = segmentCount - segmentsFilled;
                  return new string('⬛', segmentsFilled) + new string('⬜', segmentsEmpty);
               }
               return emotIcon + " " + textualProgressBar(emotionEvaluator(face.FaceAttributes.Emotion));
            }

            Rectangle faceRectangleCanvasItem(Canvas canvas, RelativeRectangle relativeRectangle)
            {
               return new Rectangle()
               {
                  Width = (uint)(canvas.ActualWidth * relativeRectangle.Width),
                  Height = (uint)(canvas.ActualHeight * relativeRectangle.Height),
                  Margin = new Thickness(
                           (uint)(canvas.ActualWidth * relativeRectangle.Left),
                           (uint)(canvas.ActualHeight * relativeRectangle.Top),
                           0, 0),
                  Fill = this.fillBrush,
                  Stroke = this.lineBrush,
                  StrokeThickness = this.lineThickness,
               };
            }

            TextBlock emotionCanvasItem(Canvas canvas, RelativeRectangle relativeRectangle, string emotionLabel, int rowNumber)
            {
               int fontSize = 24;
               int rowHeight = fontSize * 5 / 4;
               if (canvas.ActualHeight * relativeRectangle.Height < 7 * rowHeight)
               {
                  fontSize = 12;
                  rowHeight = fontSize * 5 / 4;
               }
               int leftMargin = 5 * rowHeight;
               var xPosition = (uint)(canvas.ActualWidth * (relativeRectangle.Left + relativeRectangle.Width));
               if (xPosition + leftMargin > canvas.ActualWidth)
               {
                  xPosition = (uint)(canvas.ActualWidth * relativeRectangle.Left);
               }
               var yPosition = (uint)(canvas.ActualHeight * (relativeRectangle.Top + relativeRectangle.Height) - rowNumber * rowHeight);
               return new TextBlock()
               {

                  Text = emotionLabel,
                  FontSize = fontSize,
                  FlowDirection = FlowDirection.LeftToRight,
                  Foreground = this.lineBrush,
                  Margin = new Thickness(xPosition, yPosition, 0, 0)
               };
            }

            var remoteFacesAttributes =
               from remoteFace in remoteFaces
               select
               new
               {
                  face = remoteFace.FaceId,
                  relativeRectangle = GetFaceRectangleRelativeToFrame(remoteFace, currentFrameSize),
                  emotionTextItems = new List<string>
                  {
                     emotionTextItem(remoteFace, "😠", emotion => emotion.Anger),
                     emotionTextItem(remoteFace, "🤨", emotion => emotion.Contempt),
                     emotionTextItem(remoteFace, "😫", emotion => emotion.Disgust),
                     emotionTextItem(remoteFace, "😱", emotion => emotion.Fear),
                     emotionTextItem(remoteFace, "😁", emotion => emotion.Happiness),
                     emotionTextItem(remoteFace, "☹️", emotion => emotion.Sadness),
                     emotionTextItem(remoteFace, "😮", emotion => emotion.Surprise)
                  }
               };

            foreach (var remoteFaceAttributes in remoteFacesAttributes)
            {
               this.VisualizationCanvas.Children.Add(faceRectangleCanvasItem(this.VisualizationCanvas, remoteFaceAttributes.relativeRectangle));
               for (var i = 0; i<remoteFaceAttributes.emotionTextItems.Count(); ++i)
               {
                  this.VisualizationCanvas.Children.Add(
                     emotionCanvasItem(
                        this.VisualizationCanvas,
                        remoteFaceAttributes.relativeRectangle,
                        remoteFaceAttributes.emotionTextItems[i],
                        i + 1));
               }
            }
         }
      }

      private void DisplayRelativeRectangles(Canvas canvas, IEnumerable<RelativeRectangle> relativeRectangles)
      {
         canvas.Children.Clear();

         var boxes = from rectangle in relativeRectangles
                     select new Rectangle()
                     {
                        Width = (uint)(canvas.ActualWidth * rectangle.Width),
                        Height = (uint)(canvas.ActualHeight * rectangle.Height),
                        Margin = new Thickness(
                           (uint)(canvas.ActualWidth * rectangle.Left),
                           (uint)(canvas.ActualHeight * rectangle.Top),
                           0, 0),
                        Fill = this.fillBrush,
                        Stroke = this.lineBrush,
                        StrokeThickness = this.lineThickness,
                     };

         foreach (var box in boxes)
         {
            canvas.Children.Add(box);
         }
      }

      private RelativeRectangle GetFaceRectangleRelativeToFrame(LocalDetectedFace face, Size videoFrameSize)
      {
         double left = (double)face.FaceBox.X / (double)videoFrameSize.Width;
         double top = (double)face.FaceBox.Y / (double)videoFrameSize.Height;
         double width = (double)face.FaceBox.Width / (double)videoFrameSize.Width;
         double height = (double)face.FaceBox.Height / (double)videoFrameSize.Height;
         return new RelativeRectangle(left: left, top: top, width: width, height: height);
      }

      private IList<RelativeRectangle> GetFaceRectanglesRelativeToFrame(IEnumerable<LocalDetectedFace> faces, Size videoFrameSize)
      {
         return
            (from face in faces
             select GetFaceRectangleRelativeToFrame(face, videoFrameSize)).ToList();
      }
      private RelativeRectangle GetFaceRectangleRelativeToFrame(RemoteDetectedFace face, Size videoFrameSize)
      {
         double left = (double)face.FaceRectangle.Left / (double)videoFrameSize.Width;
         double top = (double)face.FaceRectangle.Top / (double)videoFrameSize.Height;
         double width = (double)face.FaceRectangle.Width / (double)videoFrameSize.Width;
         double height = (double)face.FaceRectangle.Height / (double)videoFrameSize.Height;
         return new RelativeRectangle(left: left, top: top, width: width, height: height);
      }

      private IList<RelativeRectangle> GetFaceRectanglesRelativeToFrame(IEnumerable<RemoteDetectedFace> faces, Size videoFrameSize)
      {
         return
            (from face in faces
             select GetFaceRectangleRelativeToFrame(face, videoFrameSize)).ToList();
      }

      private async Task<IList<LocalDetectedFace>> FindFacesOnFrameLocalAsync(VideoFrame videoFrame)
      {
         return await this.faceTracker.ProcessNextFrameAsync(videoFrame);
      }

      private async Task<IList<RemoteDetectedFace>> FindFacesOnFrameRemoteAsync(VideoFrame videoFrame)
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
            return await faceClient.Face.DetectWithStreamAsync(captureStream.AsStreamForRead(), true, true, faceAttributes);
         }
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

      //private async Task ProcessCurrentVideoFrameAsync(IList<RemoteDetectedFace> remoteFaces)
      //{
      //   try
      //   {
      //      if (remoteFaces.Count > 0)
      //      {
      //         var remoteFace = remoteFaces[0];

      //         // Create our visualization using the frame dimensions and face results but run it on the UI thread.
      //         var videoFrameSize = new Windows.Foundation.Size(videoFrame.SoftwareBitmap.PixelWidth, videoFrame.SoftwareBitmap.PixelHeight);
      //         var ignored = this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
      //         {
      //            this.SetupVisualization(videoFrameSize, remoteFaces);
      //            this.SetupProgressBar(
      //               remoteFace.FaceAttributes.Emotion.Anger,
      //               remoteFace.FaceAttributes.Emotion.Contempt,
      //               remoteFace.FaceAttributes.Emotion.Disgust,
      //               remoteFace.FaceAttributes.Emotion.Fear,
      //               remoteFace.FaceAttributes.Emotion.Happiness,
      //               remoteFace.FaceAttributes.Emotion.Sadness,
      //               remoteFace.FaceAttributes.Emotion.Surprise,
      //               remoteFace.FaceAttributes.Smile.Value);
      //         });

      //         if (remoteFace.FaceAttributes.Smile.Value > smileThreshold)
      //         {
      //            var file = await captureFolder.CreateFileAsync("SmileFace.jpg", CreationCollisionOption.GenerateUniqueName);
      //            var croppedBitmap = CropImageByRect(videoFrame.SoftwareBitmap, remoteFace.FaceRectangle);
      //            await SaveSoftwareBitmapAsync(croppedBitmap, file);
      //         }
      //      }
      //   }
      //   catch (Exception ex)
      //   {
      //      var ignored = this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
      //      {
      //         NotifyUser(ex.ToString(), NotifyType.ErrorMessage);
      //      });
      //   }
      //}

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

            var propertySet = new BitmapPropertySet();
            var orientationValue = new BitmapTypedValue(
                1, // Defined as EXIF orientation = "normal"
                PropertyType.UInt16
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
