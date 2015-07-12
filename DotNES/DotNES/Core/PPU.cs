﻿using DotNES.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DotNES.Core
{
    /// <summary>
    /// http://wiki.nesdev.com/w/index.php/PPU_rendering
    /// Information on palettes: http://wiki.nesdev.com/w/index.php/PPU_palettes
    /// </summary>
    public class PPU
    {
        private Logger log = new Logger("PPU");
        public void setLoggerEnabled(bool enable)
        {
            this.log.setEnabled(enable);
        }

        private long _frameCount = 0;
        public long FrameCount
        {
            get
            {
                return _frameCount;
            }
        }

        private NESConsole console;

        private bool oddFrame = false;

        // We are not yet at a point where care to emulate the NTSC palette generation
        // If we get to that point, look at the generators/documentation 
        // on http://wiki.nesdev.com/w/index.php/PPU_palettes . For now, this is a
        // pre-computed version found on another wiki online.
        private uint[] RGBA_PALETTE = {
            0x7C7C7CFF,0x0000FCFF,0x0000BCFF,0x4428BCFF,
            0x940084FF,0xA80020FF,0xA81000FF,0x881400FF,
            0x503000FF,0x007800FF,0x006800FF,0x005800FF,
            0x004058FF,0x000000FF,0x000000FF,0x000000FF,
            0xBCBCBCFF,0x0078F8FF,0x0058F8FF,0x6844FCFF,
            0xD800CCFF,0xE40058FF,0xF83800FF,0xE45C10FF,
            0xAC7C00FF,0x00B800FF,0x00A800FF,0x00A844FF,
            0x008888FF,0x000000FF,0x000000FF,0x000000FF,
            0xF8F8F8FF,0x3CBCFCFF,0x6888FCFF,0x9878F8FF,
            0xF878F8FF,0xF85898FF,0xF87858FF,0xFCA044FF,
            0xF8B800FF,0xB8F818FF,0x58D854FF,0x58F898FF,
            0x00E8D8FF,0x787878FF,0x000000FF,0x000000FF,
            0xFCFCFCFF,0xA4E4FCFF,0xB8B8F8FF,0xD8B8F8FF,
            0xF8B8F8FF,0xF8A4C0FF,0xF0D0B0FF,0xFCE0A8FF,
            0xF8D878FF,0xD8F878FF,0xB8F8B8FF,0xB8F8D8FF,
            0x00FCFCFF,0xF8D8F8FF,0x000000FF,0x000000FF
        };

        private uint[] _imageData = new uint[256 * 240];
        public uint[] ImageData
        {
            get
            {
                return _imageData;
            }
        }

        private void drawBackground()
        {
            // NOTE HACKS TODO BBQ The general method here cannot handle scrolling whatsoever.

            // There are two background pattern table possibilities, use the one selected by PPUCTRL.
            ushort bg_pattern_base =  (ushort)((_PPUCTRL & 0x10) == 0 ? 0x0000 : 0x1000);
            ushort attribute_table_base = 0x23C0; // Attribute Table base for the first Name Table

            // Walk over each tile in the name table and draw to the output image.
            for (int nti = 0; nti < 32; ++nti)
                for (int ntj = 0; ntj < 30; ++ntj)
                {
                    int nt_index = ntj * 32 + nti;

                    // Look up in the name table what pattern table entry has this sprite's pattern
                    // (Each pattern takes 16 bytes of data to express)
                    ushort pt_index = (ushort)(RAM[0x2000 + nt_index] * 16);

                    int attributeTableIndex = (ntj / 4) * 8 + (nti / 4);
                    byte attributeTableEntry = RAM[attribute_table_base + attributeTableIndex];

                    // which pallete to derive the color from
                    int which_palette = 0;
                    which_palette |= nti % 4 >= 2 ? 2 : 0;
                    which_palette |= ntj % 4 >= 2 ? 4 : 0;

                    // Use the previous value to select the two-bit palette number
                    byte palette_num = (byte)((attributeTableEntry >> which_palette) & 3);

                    // For each pixel in the pattern
                    for (int j = 0; j < 8; ++j)
                    {
                        byte lowBits = getPatternTable((ushort)(bg_pattern_base + pt_index + j));
                        byte highBits = getPatternTable((ushort)(bg_pattern_base + pt_index + j + 8));

                        for (int i = 0; i < 8; ++i)
                        {
                            // Which color in the chosen palette
                            byte lowBit = (byte)((lowBits >> (7 - i)) & 1);
                            byte highBit = (byte)((highBits >> (7 - i)) & 1);
                            byte color_index = (byte)(lowBit + highBit * 2);

                            int image_index = (ntj * 8 + j) * 256 + (nti * 8 + i);
                            if (color_index > 0)
                            {
                                byte RGB_index = RAM[0x3F00 + 4*palette_num + color_index];
                                uint pixelColor = RGBA_PALETTE[RGB_index & 0x3F];

                                _imageData[image_index] = pixelColor;
                            }
                            else
                            {
                                uint universalBackgroundColorIndex = RAM[0x3F00];
                                uint pixelColor = RGBA_PALETTE[universalBackgroundColorIndex];
                                _imageData[image_index] = pixelColor;
                            }
                        }
                    }
                }
        }

        private void drawSprites()
        {
            uint universalBackgroundColor = RGBA_PALETTE[RAM[0x3F00]];

            // This is ignored for 8x16 sprites -- they specify the page in their attributes
            ushort sprite_pattern_table_base = (ushort)((_PPUCTRL & 8) == 0 ? 0x0000 : 0x1000);

            for (int oam_index = 0; oam_index < 256; ++oam_index)
            {
                byte sprite_y = OAM[oam_index * 4 + 0];
                byte pt_index = OAM[oam_index * 4 + 1];
                byte attributes = OAM[oam_index * 4 + 2];
                byte sprite_x = OAM[oam_index * 4 + 3];

                byte palette_number = (byte)(attributes & 3);
                bool inFrontOfBG = (attributes & 0x20) == 0;
                bool flipHorizontal = (attributes & 0x40) != 0;
                bool flipVertical = (attributes & 0x80) != 0;

                // Sprites are never drawn on if their y-coord is 0
                if (sprite_y == 0)
                    continue;

                // TODO: The following code is only valid for 8x8 sprites

                for (int tj = 0; tj < 8; ++tj)
                {
                    byte lowBits = getPatternTable((ushort)(sprite_pattern_table_base + pt_index * 16 + tj));
                    byte highBits = getPatternTable((ushort)(sprite_pattern_table_base + pt_index * 16 + tj + 8));

                    for (int ti = 0; ti < 8; ++ti)
                    {
                        // Transform from sprite coordinates to flip-corrected coordinates...
                        int i = flipHorizontal ? 7 - ti : ti;
                        int j = flipVertical ? 7 - tj : tj;

                        byte lowBit = (byte)((lowBits >> (7 - ti)) & 1);
                        byte highBit = (byte)((highBits >> (7 - ti)) & 1);
                        byte color_index = (byte)(lowBit + highBit * 2);

                        // ... and then to screen space coordinates
                        int px = sprite_x + i;
                        int py = sprite_y + j;

                        // Is this sprite's pixel on the screen?
                        if (px >= 256 || py >= 240) continue;

                        int image_index = 256 * py + px;

                        if (color_index > 0)
                        {
                            // TODO : Respect BG/Sprite precedence
                            if(inFrontOfBG || _imageData[image_index] == universalBackgroundColor)
                            {
                                uint RGB_index = RAM[0x3F10 + palette_number * 4 + color_index];
                                uint pixelColor = RGBA_PALETTE[RGB_index & 0x3F];

                                _imageData[image_index] = pixelColor;
                            }
                        }
                        else
                        {
                            // Do sprites draw a background color or simply defer to BG?
                            /*
                            uint universalBackgroundColorIndex = RAM[0x3F00];
                            uint pixelColor = RGBA_PALETTE[universalBackgroundColorIndex];
                            _imageData[image_index] = pixelColor;
                            */
                        }

                    }
                }

            }
        }

        public void assembleImage()
        {
            // clear the image
            for (int i = 0; i < _imageData.Length; ++i)
                _imageData[i] = 0x000000FF;

            // Should we draw the background?
            if ((_PPUMASK & 8) != 0)
                drawBackground();

            // Should we draw the sprites?
            if ((_PPUMASK & 0x10) != 0)
                drawSprites();

        }

        private byte[] RAM = new byte[0x4000];
        private byte[] OAM = new byte[0x400];

        // Current column and scanline (int to support pre-scanline convention -1, etc.)
        private int x, scanline;

        #region Registers

        byte _PPUCTRL;
        byte _PPUMASK;
        byte _PPUSTATUS;

        ushort _PPUSCROLL;
        ushort _PPUADDR;
        byte _PPUDATA;

        // Object Attribute Memory (OAM) is memory internal to the PPU that contains sprite 
        // data (positions, mirroring, priority, pallete, etc.) for up to 64 sprites, each taking up 4 bytes.
        byte _OAMDATA;
        byte _OAMADDR;
        byte _OAMDMA;

        #endregion

        public PPU(NESConsole console)
        {
            this.console = console;
        }

        public void render_step()
        {
            // The first pre-scanline (-1) alternates every scanline between 340 and 341 pixels.
            // For all other scanlines, the scanline is 341 pixels [0,340].
            // Actual visible pixels are [0,256), with [256,340] dedicated to preparing for sprites 
            // in the following scanline.

            // There are 262 total scanlines per frame:
            // -1      : Pre-scanline        0-239   : Visible scanlines
            // 240     : Nada?               241-260 : Vertical Blanking

            bool showBackground = (_PPUMASK & 0x80) != 0;

            // At the very start of the VBlank region, NMI is triggered.
            if (scanline >= 241 && x == 1)
            {
                _PPUSTATUS |= 0x80;

                // Potentially trigger NMI for start of VBlank
                if ((byte)(_PPUCTRL & 0x80) != 0 && scanline == 241)
                {
                    console.cpu.nmi = true;
                }
            }

            x++;

            // Variable line width for the pre-scanline depending on even/odd frame
            if (scanline == -1 && oddFrame && x == 339)
            {
                x = 0;
                scanline++;
            }
            else if (x == 340)
            {
                scanline++;
                x = 0;
            }

            if (scanline == 261)
            {
                // We're no longer in VBlank region
                _PPUSTATUS = (byte)(_PPUSTATUS & 0x7F);

                scanline = -1;
                _frameCount++;
            }
        }

        public void step()
        {
            render_step();
        }

        public void coldBoot()
        {
            _PPUCTRL = _PPUMASK = _PPUSTATUS = 0;
            _OAMADDR = 0;
            _PPUSCROLL = _PPUDATA = 0;
            _PPUADDR = 0;
            oddFrame = false;
        }

        public void warmBoot()
        {
            // AFAIK These are effectively the same except for some extremely exceptional cases
            coldBoot();
        }

        // Abstract grabbing data from the pattern tables since it could either be in VRAM, or directly mapped to CHR-ROM via the mapper
        private byte getPatternTable(ushort address)
        {
            if (console.mapper.mapsCHR())
            {
                return console.mapper.readCHR(address);
            }
            return RAM[address];
        }

        public byte read(ushort addr)
        {
            if (addr == 0x2001)
            {
                return _PPUMASK;
            }
            else if (addr == 0x2002)
            {
                // Reading the status register clears the "NMI Occurred" flag, but still returns
                // the old value of the register before clearing the flag.
                byte result = _PPUSTATUS;
                _PPUSTATUS &= 0x7F;
                return result;
            }
            else if (addr == 0x2007)
            {
                byte value = RAM[_PPUADDR & 0x3FFF];
                int increment = (_PPUCTRL & 4) == 0 ? 1 : 32;
                _PPUADDR = (ushort)((_PPUADDR + increment) & 0xFFFF);
                return value;
            }
            Console.WriteLine(string.Format("Unimplemented read to PPU @ {0:X}", addr));
            return 0;
        }

        public void write(ushort addr, byte val)
        {
            if (addr == 0x2000)
            {
                _PPUCTRL = val;
            }
            else if (addr == 0x2001)
            {
                _PPUMASK = val;
            }
            else if (addr == 0x2003)
            {
                _OAMADDR = val;
            }
            else if (addr == 0x2005)
            {
                _PPUSCROLL = (ushort)((_PPUSCROLL << 8) | (val & 0xFF));
            }
            else if (addr == 0x2006)
            {
                // The user can set a VRAM address to write to by writing to PPUADDR twice in succession.
                _PPUADDR = (ushort)((_PPUADDR << 8) | (val & 0xFF));
            }
            else if (addr == 0x2007)
            {
                // Write val to the location pointed to by PPUADDR
                RAM[_PPUADDR & 0x3FFF] = val;

                // PPUADDR increments by a configurable amount stored in PPUCTRL
                int increment = (_PPUCTRL & 4) == 0 ? 1 : 32;
                _PPUADDR = (ushort)((_PPUADDR + increment) & 0x3FFF);
            }
            else if (addr == 0x4014)
            {
                // Writing XX to 0x4014 causes CPU RAM 0xXX00-0xXXFF to be written to OAM
                for (int offset = 0; offset <= 0xFF; ++offset)
                {
                    ushort read_address = (ushort)((val << 8) + offset);
                    OAM[offset] = console.memory.read8(read_address);
                }
            }
            else
            {
                Console.Error.WriteLine("Unimplemented write {0:X2} to PPU @ {1:X4}", val, addr);
            }
        }
    }
}
