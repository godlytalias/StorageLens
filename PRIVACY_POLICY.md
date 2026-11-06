# StorageLens Privacy Policy

**Effective date:** November 7, 2026

This Privacy Policy explains how StorageLens ("StorageLens," "the App," or
"we") processes information when you use the StorageLens application for
Windows.

## Privacy summary

- StorageLens processes storage information on your device.
- StorageLens does not require an account and does not create an advertising
  profile.
- StorageLens does not send your indexed file names, folder paths, file
  contents, hashes, preferences, or cleanup records to the developer or to a
  third-party service.
- StorageLens contains no advertising SDK, analytics SDK, or application usage
  tracking service.
- Files selected for cleanup are moved to the Windows Recycle Bin. StorageLens
  does not provide a permanent-delete operation.

## Information StorageLens processes

To provide storage analysis and organization features, StorageLens reads
information available on the volumes and folders that you scan. Depending on
the feature you use, this information may include:

- file and folder names and paths;
- drive names, labels, file-system types, capacity, and free space;
- file and folder sizes, creation and modification times, attributes, and
  depth in the folder tree;
- Windows file identifiers used to recognize files across scans;
- category classifications such as applications, documents, photos, videos,
  system files, cache, and temporary files;
- scan progress, scan history, folder-growth samples, and cleanup results;
- file selections and cleanup policy choices; and
- SHA-256 sample or content hashes used to compare possible duplicate files.

For duplicate detection, StorageLens may read portions or the complete content
of candidate files to calculate a cryptographic hash. The App stores the
resulting hash value in its local storage index; it does not copy or retain
the original file contents in that index.

StorageLens also stores settings required for its operation, such as your
theme, scan preferences, category visibility, cleanup safety options,
notification preferences, scheduled review choices, and monitoring cadence.

If the App fails during startup or runtime initialization, it may create a
local diagnostic file containing an error message and stack trace. This file
can contain local file paths or other technical details relevant to the
failure. It is stored on the device and is removed after a later successful
startup initialization. StorageLens does not automatically send this file to
the developer.

## How the information is used

StorageLens uses the information described above only to provide and maintain
the App's functionality on your device, including:

- displaying drive and folder usage;
- ranking large files and space-consuming folders;
- identifying cleanup opportunities and possible duplicate files;
- comparing scans to identify folder growth;
- validating files before cleanup;
- moving eligible, user-selected items to the Windows Recycle Bin;
- maintaining an on-device index and scan history; and
- running user-enabled background checks and showing local Windows
  notifications.

The App does not use this information for advertising, cross-app tracking,
profiling, or sale.

## Storage and transmission

StorageLens stores its index, settings, monitoring state, schedules, scan
history, and cleanup records in the App's local Windows application data
location. The index is a local SQLite database. This data remains on your
device.

StorageLens does not upload or transmit:

- file names or paths;
- folder trees or drive inventories;
- file contents;
- duplicate hashes;
- cleanup selections or cleanup history;
- settings, schedules, or scan results; or
- local diagnostic files.

Windows and the Microsoft Store may process information required for Windows
operation, app installation, licensing, updates, and security under Microsoft's
own privacy policies. That Microsoft processing is separate from StorageLens
and is not controlled by this policy.

## Background processing and notifications

If you enable monitoring or idle indexing, StorageLens may run Windows
background tasks to check available drive space, refresh stored usage
information, and compare folder-growth samples. Background processing occurs
locally on the device.

Notifications are displayed by Windows for low-storage conditions, growth
alerts, or scheduled cleanup-review reminders according to your App and
Windows notification settings. StorageLens does not send these notifications
through an external service.

## Cleanup and file safety

StorageLens follows a review-first cleanup process. It asks you to review and
confirm the applicable cleanup choices before moving eligible items to the
Windows Recycle Bin. Before a cleanup, the App rechecks relevant file
information. Files that are protected, unavailable, cloud-only, or detected as
changed or otherwise ineligible are excluded according to the App's safety
rules.

Moving a file to the Recycle Bin is a local Windows file operation. StorageLens
does not permanently delete the file as part of its cleanup operation. You can
restore or permanently delete recycled items by using Windows.

## Sharing and disclosure

StorageLens does not sell, rent, share, or disclose your indexed information,
file contents, hashes, settings, or cleanup records.

The App also does not integrate with third-party analytics, advertising,
crash-reporting, or cloud-storage services.

## Your choices and controls

You control whether and when to:

- scan available drives or folders;
- enable or disable background monitoring and idle indexing;
- enable local notifications;
- create, edit, or remove cleanup-review schedules;
- include hidden or system items in supported views;
- select individual cleanup candidates; and
- move selected eligible files to the Windows Recycle Bin.

You can reset the App's preferences from Settings. To remove the locally
stored index, preferences, schedules, and other App data, use the reset or
uninstall controls provided by Windows for StorageLens. Uninstalling the App
also removes its installed package; Windows App data may be removed as part of
that process.

If StorageLens is installed from the Microsoft Store, Microsoft may retain
separate account or purchase information as described in the applicable
Microsoft privacy policy.

## Retention

StorageLens retains local scan data and settings for as long as they remain
needed in the App's local data store. This lets the App show prior scan results,
detect growth, resume interrupted scans, and maintain cleanup records.

You can remove this local data by resetting StorageLens through Windows app
settings or by uninstalling StorageLens. Compacting the local index reduces
database storage overhead but does not erase the index.

## Security

StorageLens relies on the Windows application and file-system security model.
Access to local App data and files is subject to your Windows account,
permissions, and device security settings. No method of local storage or
electronic transmission is completely secure, but StorageLens minimizes
privacy exposure by keeping its operational data on your device and avoiding
cloud transmission.

## Children's privacy

StorageLens is a general-purpose storage management application and does not
knowingly collect personal information from children or require a child to
create an account.

## Changes to this policy

We may update this Privacy Policy when StorageLens functionality or data
handling changes. The latest version will be identified by its effective date.
Material changes will be reflected in the Store listing or the App's published
privacy-policy location.

## Contact

For privacy questions or requests concerning StorageLens, contact the
developer through the support contact published on the StorageLens Microsoft
Store listing.
