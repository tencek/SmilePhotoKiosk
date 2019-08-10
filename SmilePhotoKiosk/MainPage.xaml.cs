using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.IO;

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

// The Blank Page item template is documented at https://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace SmilePhotoKiosk
{
   public enum NotifyType
   {
      StatusMessage,
      ErrorMessage
   };

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
        /// Holds the current scenario state value.
        /// </summary>
        private ScenarioState currentState;

        /// <summary>
        /// References a MediaCapture instance; is null when not in Streaming state.
        /// </summary>
        private MediaCapture mediaCapture;

        /// <summary>
        /// Cache of properties from the current MediaCapture device which is used for capturing the preview frame.
        /// </summary>
        private VideoEncodingProperties videoProperties;

        /// <summary>
        /// References a FaceTracker instance.
        /// </summary>
        private FaceTracker faceTracker;

        /// <summary>
        /// A periodic timer to execute FaceTracker on preview frames
        /// </summary>
        private ThreadPoolTimer frameProcessingTimer;

        /// <summary>
        /// Semaphore to ensure FaceTracking logic only executes one at a time
        /// </summary>
        private SemaphoreSlim frameProcessingSemaphore = new SemaphoreSlim(1);

        private const string faceEndpoint = "https://emotionalphotokiosk.cognitiveservices.azure.com";

        private IFaceClient faceClient;

        /// <summary>
        /// Initializes a new instance of the <see cref="TrackFacesInWebcam"/> class.
        /// </summary>
        public MainPage()
        {
            this.InitializeComponent();

            this.currentState = ScenarioState.Idle;
            App.Current.Suspending += this.OnSuspending;
        }

        /// <summary>
        /// Values for identifying and controlling scenario states.
        /// </summary>
        private enum ScenarioState
        {
            /// <summary>
            /// Display is blank - default state.
            /// </summary>
            Idle,

            /// <summary>
            /// Webcam is actively engaged and a live video stream is displayed.
            /// </summary>
            Streaming
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
            // The 'await' operation can only be used from within an async method but class constructors
            // cannot be labeled as async, and so we'll initialize FaceTracker here.
            if (this.faceTracker == null)
            {
                this.faceTracker = await FaceTracker.CreateAsync();
            }
        }

        /// <summary>
        /// Responds to App Suspend event to stop/release MediaCapture object if it's running and return to Idle state.
        /// </summary>
        /// <param name="sender">The source of the Suspending event</param>
        /// <param name="e">Event data</param>
        private void OnSuspending(object sender, Windows.ApplicationModel.SuspendingEventArgs e)
        {
            if (this.currentState == ScenarioState.Streaming)
            {
                var deferral = e.SuspendingOperation.GetDeferral();
                try
                {
                    this.ChangeScenarioState(ScenarioState.Idle);
                }
                finally
                {
                    deferral.Complete();
                }
            }

            ApplicationData.Current.LocalSettings.Values["ApiKey"] = ApiKey.Text;
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

                // For this scenario, we only need Video (not microphone) so specify this in the initializer.
                // NOTE: the appxmanifest only declares "webcam" under capabilities and if this is changed to include
                // microphone (default constructor) you must add "microphone" to the manifest or initialization will fail.
                MediaCaptureInitializationSettings settings = new MediaCaptureInitializationSettings();
                settings.StreamingCaptureMode = StreamingCaptureMode.Video;
                await this.mediaCapture.InitializeAsync(settings);
                this.mediaCapture.Failed += this.MediaCapture_CameraStreamFailed;

                // Cache the media properties as we'll need them later.
                var deviceController = this.mediaCapture.VideoDeviceController;
                this.videoProperties = deviceController.GetMediaStreamProperties(MediaStreamType.VideoPreview) as VideoEncodingProperties;

                // Immediately start streaming to our CaptureElement UI.
                // NOTE: CaptureElement's Source must be set before streaming is started.
                this.CamPreview.Source = this.mediaCapture;
                await this.mediaCapture.StartPreviewAsync();

                TimeSpan timerInterval = TimeSpan.FromMilliseconds(200);
                this.frameProcessingTimer = Windows.System.Threading.ThreadPoolTimer.CreatePeriodicTimer(new Windows.System.Threading.TimerElapsedHandler(ProcessCurrentVideoFrameAsync), timerInterval);
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

        /// <summary>
        /// Safely stops webcam streaming (if running) and releases MediaCapture object.
        /// </summary>
        private async void ShutdownWebCam()
        {
            if(this.frameProcessingTimer != null)
            {
                this.frameProcessingTimer.Cancel();
            }

            if (this.mediaCapture != null)
            {
                if (this.mediaCapture.CameraStreamState == Windows.Media.Devices.CameraStreamState.Streaming)
                {
                    try
                    {
                        await this.mediaCapture.StopPreviewAsync();
                    }
                    catch(Exception)
                    {
                        ;   // Since we're going to destroy the MediaCapture object there's nothing to do here
                    }
                }
                this.mediaCapture.Dispose();
            }

            this.frameProcessingTimer = null;
            this.CamPreview.Source = null;
            this.mediaCapture = null;
            this.CameraStreamingButton.IsEnabled = true;

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
        private async void ProcessCurrentVideoFrameAsync(ThreadPoolTimer timer)
        {
            if (this.currentState != ScenarioState.Streaming)
            {
                return;
            }

            // If a lock is being held it means we're still waiting for processing work on the previous frame to complete.
            // In this situation, don't wait on the semaphore but exit immediately.
            if (!frameProcessingSemaphore.Wait(0))
            {
                return;
            }

            try
            {
                IList<LocalDetectedFace> faces = null;
                var anger = 0.0;
                var contempt = 0.0;
                var disgust = 0.0;
                var fear = 0.0;
                var happiness = 0.0;
                var sadness = 0.0;
                var surprise = 0.0;
                var smile = 0.0;

                // Create a VideoFrame object specifying the pixel format we want our capture image to be (NV12 bitmap in this case).
                // GetPreviewFrame will convert the native webcam frame into this format.
                const BitmapPixelFormat InputPixelFormat = BitmapPixelFormat.Nv12;
                using (VideoFrame previewFrame = new VideoFrame(InputPixelFormat, (int)this.videoProperties.Width, (int)this.videoProperties.Height))
                {
                    await this.mediaCapture.GetPreviewFrameAsync(previewFrame);

                    // The returned VideoFrame should be in the supported NV12 format but we need to verify this.
                    if (FaceDetector.IsBitmapPixelFormatSupported(previewFrame.SoftwareBitmap.BitmapPixelFormat))
                    {
                        faces = await this.faceTracker.ProcessNextFrameAsync(previewFrame);
                    }
                    else
                    {
                        throw new System.NotSupportedException("PixelFormat '" + InputPixelFormat.ToString() + "' is not supported by FaceDetector");
                    }

                    if (faces.Count > 0)
                    {
                        IList<FaceAttributeType> faceAttributes =
                            new FaceAttributeType[]
                            {
                                    FaceAttributeType.Gender, FaceAttributeType.Age,
                                    FaceAttributeType.Smile, FaceAttributeType.Emotion
                            };
                        using (var captureStream = new InMemoryRandomAccessStream())
                        {
                            double evaluate(IList<RemoteDetectedFace> detectedFaceList, Func<FaceAttributes, double?> selector) =>
                                (from detectedFace in detectedFaceList
                                 where selector(detectedFace.FaceAttributes).HasValue
                                 select selector(detectedFace.FaceAttributes).Value).Max();

                            await mediaCapture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), captureStream);
                            captureStream.Seek(0);
                            IList<RemoteDetectedFace> faceList =
                                    await faceClient.Face.DetectWithStreamAsync(captureStream.AsStreamForRead(), true, true, faceAttributes);
                            if (faceList.Count > 0)
                            {
                                anger = evaluate(faceList, (x => x.Emotion.Anger));
                                contempt = evaluate(faceList, (x => x.Emotion.Contempt));
                                disgust = evaluate(faceList, (x => x.Emotion.Disgust));
                                fear = evaluate(faceList, (x => x.Emotion.Fear));
                                happiness = evaluate(faceList, (x => x.Emotion.Happiness));
                                sadness = evaluate(faceList, (x => x.Emotion.Sadness));
                                surprise = evaluate(faceList, (x => x.Emotion.Surprise));
                                smile = evaluate(faceList, (x => x.Smile));
                            }
                        }
                    }

                    // Create our visualization using the frame dimensions and face results but run it on the UI thread.
                    var previewFrameSize = new Windows.Foundation.Size(previewFrame.SoftwareBitmap.PixelWidth, previewFrame.SoftwareBitmap.PixelHeight);
                    var ignored = this.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        //this.SetupVisualization(previewFrameSize, faces);
                        this.SetupProgressBar(anger, contempt, disgust, fear, happiness, sadness, surprise, smile);
                    });
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
        private void SetupVisualization(Windows.Foundation.Size framePixelSize, IList<LocalDetectedFace> foundFaces)
        {
            this.VisualizationCanvas.Children.Clear();

            double actualWidth = this.VisualizationCanvas.ActualWidth;
            double actualHeight = this.VisualizationCanvas.ActualHeight;

            if (this.currentState == ScenarioState.Streaming && foundFaces != null && actualWidth != 0 && actualHeight != 0)
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
        /// Manages the scenario's internal state. Invokes the internal methods and updates the UI according to the
        /// passed in state value. Handles failures and resets the state if necessary.
        /// </summary>
        /// <param name="newState">State to switch to</param>
        private async void ChangeScenarioState(ScenarioState newState)
        {
            // Disable UI while state change is in progress
            this.CameraStreamingButton.IsEnabled = false;

            switch (newState)
            {
                case ScenarioState.Idle:

                    this.ShutdownWebCam();

                    this.VisualizationCanvas.Children.Clear();
                    this.CameraStreamingButton.Content = "Start Streaming";
                    this.currentState = newState;
                    this.faceClient.Dispose();
                    this.faceClient = null;
                    break;

                case ScenarioState.Streaming:

                    faceClient = new FaceClient(
                        new ApiKeyServiceClientCredentials(ApiKey.Text),
                        new System.Net.Http.DelegatingHandler[] { })
                        {
                            Endpoint = faceEndpoint
                        };
                        if (!await this.StartWebcamStreaming())
                    {
                        this.ChangeScenarioState(ScenarioState.Idle);
                        break;
                    }

                    this.VisualizationCanvas.Children.Clear();
                    this.CameraStreamingButton.Content = "Stop Streaming";
                    this.currentState = newState;
                    this.CameraStreamingButton.IsEnabled = true;
                    break;
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
                ChangeScenarioState(ScenarioState.Idle);
            });
        }

        /// <summary>
        /// Handles "streaming" button clicks to start/stop webcam streaming.
        /// </summary>
        /// <param name="sender">Button user clicked</param>
        /// <param name="e">Event data</param>
        private void CameraStreamingButton_Click(object sender, RoutedEventArgs e)
        {
            if (this.currentState == ScenarioState.Streaming)
            {
                NotifyUser(string.Empty, NotifyType.StatusMessage);
                this.ChangeScenarioState(ScenarioState.Idle);
            }
            else
            {
                NotifyUser(string.Empty, NotifyType.StatusMessage);
                this.ChangeScenarioState(ScenarioState.Streaming);
            }
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
   }
}
