-- Schema v4: media from other folders and drives (USB drives, other shares) besides the root folder.
ALTER TABLE InventoryConfig ADD COLUMN AllowExternalMedia INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Media ADD COLUMN SourcePath TEXT;      -- NULL: <root>\<MediaID>; else the media's own folder
ALTER TABLE Media ADD COLUMN VolumeSerial TEXT;    -- local drive serial (1A2B-3C4D): finds the drive under another letter
ALTER TABLE Media ADD COLUMN VolumeLabel TEXT;
ALTER TABLE Media ADD COLUMN AddedOnMachine TEXT;
