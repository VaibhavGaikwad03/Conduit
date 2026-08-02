package io.conduit.features

import android.content.ContentUris
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import io.conduit.logging.ConduitLog
import java.io.File
import java.util.UUID

/**
 * Backs the cross-device file-search feature on the phone: finds files whose name contains a
 * query via MediaStore (Downloads, Documents, images/video/audio — no extra permission), and
 * remembers each hit under an opaque id so the PC can download it. A `file-request` is only
 * honored for an id from a recent result, so the peer can never pull an arbitrary file.
 */
class FileSearchFeature(private val context: Context) {
    private val log = ConduitLog.tag("FileSearch")

    /** One match; [id] is the opaque token the peer echoes back to download it. */
    data class Result(
        val id: String,
        val name: String,
        val size: Long,
        val folder: String,
        val mime: String,
    )

    // Opaque id -> content URI, bounded so stale tokens age out (insertion-ordered = FIFO).
    private val tokens = object : LinkedHashMap<String, Uri>() {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<String, Uri>?) = size > MAX_TOKENS
    }

    /**
     * Searches by filename substring; returns the matches and whether the list was capped.
     * With "All files access" granted we walk shared storage directly (finds APKs, docs, zips
     * — everything); otherwise we fall back to MediaStore (media + Downloads only).
     */
    @Synchronized
    fun search(query: String, isCancelled: () -> Boolean = { false }): Pair<List<Result>, Boolean> {
        val q = query.trim()
        if (q.length < MIN_QUERY_LEN) return emptyList<Result>() to false
        return if (hasAllFilesAccess()) searchFileSystem(q, isCancelled) else searchMediaStore(q, isCancelled)
    }

    private fun hasAllFilesAccess(): Boolean =
        Build.VERSION.SDK_INT >= Build.VERSION_CODES.R && Environment.isExternalStorageManager()

    /** Recursively walks shared storage (skipping the app-private Android/ tree) for name matches. */
    private fun searchFileSystem(q: String, isCancelled: () -> Boolean): Pair<List<Result>, Boolean> {
        val results = mutableListOf<Result>()
        val root = Environment.getExternalStorageDirectory() ?: return results to false
        val needle = q.lowercase()
        var truncated = false
        val stack = ArrayDeque<File>()
        stack.addLast(root)
        while (stack.isNotEmpty()) {
            if (isCancelled()) return results to truncated
            val children = stack.removeLast().listFiles() ?: continue
            for (f in children) {
                if (f.isDirectory) {
                    // Skip Android/ (data/obb/media) — huge and mostly inaccessible even here.
                    if (!(f.parentFile == root && f.name == "Android")) stack.addLast(f)
                } else if (f.name.lowercase().contains(needle)) {
                    if (results.size >= MAX_RESULTS) { truncated = true; break }
                    val token = UUID.randomUUID().toString().replace("-", "")
                    tokens[token] = Uri.fromFile(f)
                    results.add(Result(token, f.name, f.length(), f.parentFile?.name ?: "", guessMime(f.name)))
                }
            }
            if (truncated) break
        }
        log.i("Search '$q' (all-files) -> ${results.size} result(s)${if (truncated) " (truncated)" else ""}")
        return results to truncated
    }

    private fun searchMediaStore(q: String, isCancelled: () -> Boolean): Pair<List<Result>, Boolean> {
        val results = mutableListOf<Result>()
        val collection = MediaStore.Files.getContentUri("external")
        val projection = arrayOf(
            MediaStore.Files.FileColumns._ID,
            MediaStore.Files.FileColumns.DISPLAY_NAME,
            MediaStore.Files.FileColumns.SIZE,
            MediaStore.Files.FileColumns.MIME_TYPE,
            MediaStore.Files.FileColumns.RELATIVE_PATH,
        )
        val selection = "${MediaStore.Files.FileColumns.DISPLAY_NAME} LIKE ?"
        val args = arrayOf("%$q%")
        val sort = "${MediaStore.Files.FileColumns.DATE_MODIFIED} DESC"

        var truncated = false
        try {
            context.contentResolver.query(collection, projection, selection, args, sort)?.use { c ->
                val idCol = c.getColumnIndexOrThrow(MediaStore.Files.FileColumns._ID)
                val nameCol = c.getColumnIndexOrThrow(MediaStore.Files.FileColumns.DISPLAY_NAME)
                val sizeCol = c.getColumnIndexOrThrow(MediaStore.Files.FileColumns.SIZE)
                val mimeCol = c.getColumnIndexOrThrow(MediaStore.Files.FileColumns.MIME_TYPE)
                val pathCol = c.getColumnIndex(MediaStore.Files.FileColumns.RELATIVE_PATH)
                while (c.moveToNext()) {
                    if (isCancelled()) break
                    if (results.size >= MAX_RESULTS) { truncated = true; break }
                    val name = c.getString(nameCol) ?: continue
                    val size = c.getLong(sizeCol)
                    val mime = c.getString(mimeCol) ?: "application/octet-stream"
                    val uri = ContentUris.withAppendedId(collection, c.getLong(idCol))
                    val relPath = if (pathCol >= 0) c.getString(pathCol) ?: "" else ""
                    val folder = relPath.trimEnd('/').substringAfterLast('/')
                    val token = UUID.randomUUID().toString().replace("-", "")
                    tokens[token] = uri
                    results.add(Result(token, name, size, folder, mime))
                }
            }
        } catch (e: Exception) {
            log.w(e, "File search failed")
        }
        log.i("Search '$q' -> ${results.size} result(s)${if (truncated) " (truncated)" else ""}")
        return results to truncated
    }

    /** The content URI for an id we previously returned, or null if unknown/expired. */
    @Synchronized
    fun resolve(id: String): Uri? = tokens[id]

    private fun guessMime(name: String): String = when (name.substringAfterLast('.', "").lowercase()) {
        "jpg", "jpeg" -> "image/jpeg"
        "png" -> "image/png"
        "gif" -> "image/gif"
        "mp4" -> "video/mp4"
        "mp3" -> "audio/mpeg"
        "pdf" -> "application/pdf"
        "apk" -> "application/vnd.android.package-archive"
        "zip" -> "application/zip"
        else -> "application/octet-stream"
    }

    private companion object {
        const val MAX_RESULTS = 100
        const val MAX_TOKENS = 1000
        const val MIN_QUERY_LEN = 2
    }
}
