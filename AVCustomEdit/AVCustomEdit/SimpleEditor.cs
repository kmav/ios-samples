using AVFoundation;
using CoreMedia;
using Foundation;
using System.Collections.Generic;

namespace AVCustomEdit
{
    public class SimpleEditor : NSObject
    {
        private AVMutableComposition composition;
        private AVMutableVideoComposition videoComposition;

        public List<AVAsset> Clips { get; set; } // array of AVURLAssets
        public List<NSValue> ClipTimeRanges { get; set; } // array of CMTimeRanges stored in NSValues.

        public TransitionType TransitionType { get; set; }
        public CMTime TransitionDuration { get; set; }

        public AVPlayerItem PlayerItem => new AVPlayerItem(this.composition) { VideoComposition = videoComposition };

        private void BuildTransitionComposition(AVMutableComposition mutableComposition, AVMutableVideoComposition mutableVideoComposition)
        {
            var nextClipStartTime = CMTime.Zero;
            var clipsCount = this.Clips.Count;

            // Make transitionDuration no greater than half the shortest clip duration.
            var transitionDuration = this.TransitionDuration;
            foreach (var clipTimeRange in this.ClipTimeRanges)
            {
                if (clipTimeRange != null)
                {
                    var halfClipDuration = clipTimeRange.CMTimeRangeValue.Duration;
                    halfClipDuration.TimeScale *= 2; // You can halve a rational by doubling its denominator.
                    transitionDuration = CMTime.GetMinimum(transitionDuration, halfClipDuration);
                }
            }

            // Add two video tracks and two audio tracks.
            var compositionVideoTracks = new AVMutableCompositionTrack[2];
            var compositionAudioTracks = new AVMutableCompositionTrack[2];

            compositionVideoTracks[0] = mutableComposition.AddMutableTrack(AVMediaType.Video, 0);
            compositionVideoTracks[1] = mutableComposition.AddMutableTrack(AVMediaType.Video, 0);
            compositionAudioTracks[0] = mutableComposition.AddMutableTrack(AVMediaType.Audio, 0);
            compositionAudioTracks[1] = mutableComposition.AddMutableTrack(AVMediaType.Audio, 0);

            var passThroughTimeRanges = new CMTimeRange[clipsCount];
            var transitionTimeRanges = new CMTimeRange[clipsCount];

            // Place clips into alternating video & audio tracks in composition, overlapped by transitionDuration.
            for (int i = 0; i < clipsCount; i++)
            {
                int alternatingIndex = i % 2; // alternating targets: 0, 1, 0, 1, ...
                var asset = this.Clips[i];
                var clipTimeRange = this.ClipTimeRanges[i];

                var timeRangeInAsset = clipTimeRange != null ? clipTimeRange.CMTimeRangeValue
                                                             : new CMTimeRange { Start = CMTime.Zero, Duration = asset.Duration };

                var clipVideoTrack = asset.TracksWithMediaType(AVMediaType.Video)[0];
                compositionVideoTracks[alternatingIndex].InsertTimeRange(timeRangeInAsset, clipVideoTrack, nextClipStartTime, out NSError error);

                var clipAudioTrack = asset.TracksWithMediaType(AVMediaType.Audio)[0];
                compositionAudioTracks[alternatingIndex].InsertTimeRange(timeRangeInAsset, clipAudioTrack, nextClipStartTime, out error);

                // Remember the time range in which this clip should pass through.
                // First clip ends with a transition.
                // Second clip begins with a transition.
                // Exclude that transition from the pass through time ranges
                passThroughTimeRanges[i] = new CMTimeRange { Start = nextClipStartTime, Duration = timeRangeInAsset.Duration };

                if (i > 0)
                {
                    passThroughTimeRanges[i].Start = CMTime.Add(passThroughTimeRanges[i].Start, transitionDuration);
                    passThroughTimeRanges[i].Duration = CMTime.Subtract(passThroughTimeRanges[i].Duration, transitionDuration);
                }

                if (i + 1 < clipsCount)
                {
                    passThroughTimeRanges[i].Duration = CMTime.Subtract(passThroughTimeRanges[i].Duration, transitionDuration);
                }

                // The end of this clip will overlap the start of the next by transitionDuration.
                // (Note: this arithmetic falls apart if timeRangeInAsset.duration < 2 * transitionDuration.)
                nextClipStartTime = CMTime.Add(nextClipStartTime, timeRangeInAsset.Duration);
                nextClipStartTime = CMTime.Subtract(nextClipStartTime, transitionDuration);

                // Remember the time range for the transition to the next item.
                if (i + 1 < clipsCount)
                {
                    transitionTimeRanges[i] = new CMTimeRange { Start = nextClipStartTime, Duration = transitionDuration };
                }
            }

            // Set up the video composition to perform cross dissolve or diagonal wipe transitions between clips.
            var instructions = new List<AVVideoCompositionInstruction>();

            // Cycle between "pass through A", "transition from A to B", "pass through B"
            for (int i = 0; i < clipsCount; i++)
            {
                int alternatingIndex = i % 2; // alternating targets

                if (mutableVideoComposition.CustomVideoCompositorClass != null)
                {
                    var videoInstruction = new CustomVideoCompositionInstruction(compositionVideoTracks[alternatingIndex].TrackID, passThroughTimeRanges[i]);
                    instructions.Add(videoInstruction);
                }
                else
                {
                    // Pass through clip i.
                    var passThroughInstruction = AVMutableVideoCompositionInstruction.Create() as AVMutableVideoCompositionInstruction;
                    passThroughInstruction.TimeRange = passThroughTimeRanges[i];
                   
                    var passThroughLayer = AVMutableVideoCompositionLayerInstruction.FromAssetTrack(compositionVideoTracks[alternatingIndex]);
                    passThroughInstruction.LayerInstructions = new[] { passThroughLayer };

                    instructions.Add(passThroughInstruction);
                }

                if (i + 1 < clipsCount)
                {
                    // Add transition from clip i to clip i+1.
                    if (mutableVideoComposition.CustomVideoCompositorClass != null)
                    {
                        var videoInstruction = new CustomVideoCompositionInstruction(new NSNumber[] {
                                              compositionVideoTracks [0].TrackID,
                                              compositionVideoTracks [1].TrackID
                                          }, transitionTimeRanges[i]);

                        if (alternatingIndex == 0)
                        {
                            // First track -> Foreground track while compositing
                            videoInstruction.ForegroundTrackId = compositionVideoTracks[alternatingIndex].TrackID;
                            // Second track -> Background track while compositing
                            videoInstruction.BackgroundTrackId = compositionVideoTracks[1 - alternatingIndex].TrackID;
                        }

                        instructions.Add(videoInstruction);
                    }
                    else
                    {
                        var transitionInstruction = AVMutableVideoCompositionInstruction.Create() as AVMutableVideoCompositionInstruction;
                        transitionInstruction.TimeRange = transitionTimeRanges[i];

                        var fromLayer = AVMutableVideoCompositionLayerInstruction.FromAssetTrack(compositionVideoTracks[alternatingIndex]);
                        var toLayer = AVMutableVideoCompositionLayerInstruction.FromAssetTrack(compositionVideoTracks[1 - alternatingIndex]);

                        transitionInstruction.LayerInstructions = new[] { toLayer, fromLayer };
                        instructions.Add(transitionInstruction);
                    }
                }
            }

            mutableVideoComposition.Instructions = instructions.ToArray();
        }

        public void BuildCompositionObjectsForPlayback(bool playBack)
        {
            if (Clips == null || Clips.Count == 0)
            {
                this.composition = null;
                this.videoComposition = null;
                return;
            }

            var videoSize = Clips[0].NaturalSize;
            var composition = AVMutableComposition.Create();
            AVMutableVideoComposition videoComposition = null;

            composition.NaturalSize = videoSize;

            // With transitions:
            // Place clips into alternating video & audio tracks in composition, overlapped by transitionDuration.
            // Set up the video composition to cycle between "pass through A", "transition from A to B",
            // "pass through B".

            videoComposition = AVMutableVideoComposition.Create();

            if (TransitionType == TransitionType.DiagonalWipeTransition)
            {
                //videoComposition.CustomVideoCompositorClass = new ObjCRuntime.Class(typeof(DiagonalWipeCompositor));
            }
            else
            {
                //videoComposition.CustomVideoCompositorClass = new ObjCRuntime.Class(typeof(CrossDissolveCompositor));
            }

            BuildTransitionComposition(composition, videoComposition);
            if (videoComposition != null)
            {
                // Every videoComposition needs these properties to be set:
                videoComposition.FrameDuration = new CMTime(1, 30);
                videoComposition.RenderSize = videoSize;
            }

            this.composition = composition;
            this.videoComposition = videoComposition;
        }

        public AVAssetExportSession AssetExportSessionWithPreset(string presetName)
        {
            return new AVAssetExportSession(composition, presetName)
            {
                VideoComposition = videoComposition
            };
        }
    }
}