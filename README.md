# BassRouter

Simple windows utility to route audio between headphones and a subwoofer.

## Why this exists

- Route treble to headphones and bass to subwoofer so that you can hear the vocals crystal clear but still get that room shaking effect
- This has been possible before with tools like Voicemeeter but those were too heavy and complex for my liking

> This is a purpose-built tool designed to do only one thing. Please do not open feature requests beyond the scope of this project.

## Features

- Adjust latency so that the headphones and speaker are perfectly synced up
- Adjust the low pass frequency to the range supported by your subwoofer

## Requirements

You need *some sort* of kernel-level virtual audio driver. I just use the steam streaming speaker driver but it should support anything.

The reason this util doesn't come with it's own drivers is because you need a special EV certificate to sign them. So we just need to piggyback off an existing driver unfortunately.

## Limitations

- Your headphones and speakers need to have the same sample rate otherwise you might get some artifacting.
- Enabling spacial audio like windows sonic or dolby atmos might cause issues.