package com.quintar.video;

import android.app.Activity;
import android.graphics.SurfaceTexture;
import android.net.Uri;
import android.os.Handler;
import android.os.Looper;
import android.view.Surface;
import androidx.media3.common.MediaItem;
import androidx.media3.common.Player;
import androidx.media3.exoplayer.ExoPlayer;
import android.util.Log;

public class ExoPlayerBridge {
    private static final String TAG = "UnityST_Exo";
    private ExoPlayer player;
    private Surface surface;
    private SurfaceTexture surfaceTexture;
    private volatile boolean isInitialized = false;
    private volatile boolean isPrepared = false;
    private int width = 0;
    private int height = 0;
    private int textureId = -1;
    private final Handler mainHandler = new Handler(Looper.getMainLooper());

    public ExoPlayerBridge(final Activity activity, final String url, final int existingTextureId) {
        Log.d(TAG, "ExoPlayerBridge Build 38 Starting with Unity ID: " + existingTextureId);
        this.textureId = existingTextureId;

        activity.runOnUiThread(new Runnable() {
            @Override
            public void run() {
                try {
                    player = new ExoPlayer.Builder(activity)
                        .setLooper(Looper.getMainLooper())
                        .build();
                    
                    if (textureId > 0) {
                        surfaceTexture = new SurfaceTexture(textureId);
                        surfaceTexture.setDefaultBufferSize(3840, 4320);
                        surface = new Surface(surfaceTexture);
                        player.setVideoSurface(surface);
                    } else {
                        Log.e(TAG, "FATAL: Received invalid texture ID from Unity: " + textureId);
                    }

                    MediaItem mediaItem = MediaItem.fromUri(Uri.parse(url));
                    player.setMediaItem(mediaItem);
                    player.setRepeatMode(Player.REPEAT_MODE_ALL);
                    
                    player.addListener(new Player.Listener() {
                        @Override
                        public void onPlaybackStateChanged(int state) {
                            if (state == Player.STATE_READY) {
                                isPrepared = true;
                                width = player.getVideoSize().width;
                                height = player.getVideoSize().height;
                                Log.d(TAG, "ExoPlayer Build 38 READY: " + width + "x" + height);
                            }
                        }
                    });

                    player.prepare();
                    isInitialized = true;
                } catch (Exception e) {
                    Log.e(TAG, "UI Thread Error in Build 38: " + e.getMessage());
                }
            }
        });
    }

    public void play() {
        mainHandler.post(new Runnable() {
            @Override public void run() {
                try { if (player != null) player.play(); }
                catch (Exception e) { Log.e(TAG, "play() error: " + e.getMessage()); }
            }
        });
    }

    public void pause() {
        mainHandler.post(new Runnable() {
            @Override public void run() {
                try { if (player != null) player.pause(); }
                catch (Exception e) { Log.e(TAG, "pause() error: " + e.getMessage()); }
            }
        });
    }

    public boolean isInitialized() { return isInitialized; }
    public boolean isPrepared() { return isPrepared; }

    public void updateTexture() {
        if (surfaceTexture != null) {
            try {
                surfaceTexture.updateTexImage();
            } catch (Exception e) { }
        }
    }

    public void release() {
        mainHandler.post(new Runnable() {
            @Override public void run() {
                try {
                    if (player != null) player.release();
                    if (surface != null) surface.release();
                    if (surfaceTexture != null) surfaceTexture.release();
                } catch (Exception e) { Log.e(TAG, "release() error: " + e.getMessage()); }
            }
        });
    }
}
