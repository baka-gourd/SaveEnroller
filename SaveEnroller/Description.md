# Save Enroller

## What's the difference between "Save Enroller" and "Save Checker"?

Save Enroller is a auto backup mod and running at gaming, Save Checker is a check mod running at game close.

The purpose of Checker is to check for corrupted archives and move them to suppress gameplay errors. You can also get the Save Checker functionality in SMC+, but you need to manually verify the saves.

Enroller's purpose is to fix versioning issues like aaa 1, aaa2, aaa2.3, aaa4. So the only thing the two modules have in common are the letters "Save".

## Information

**The current auto-delete strategy is not fully tested because the time span is too long. But it should be working properly.**

Clean up old versions according to the retention policy:

- Keep all versions from the last day
- Keep 3 versions per day for 1-7 days old versions
- Keep 4 versions total for 7-30 days old versions
- Keep only 1 version per month for versions older than 30 days
- If total backup size exceeds 10GB, delete older versions (preferably non-monthly archives)

## Problem

I don't have an idea for a good working backup recovery interface, so ... currently just backs up and restores are completely manual.
