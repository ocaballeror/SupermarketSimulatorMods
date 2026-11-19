# Price Markup Keeper

Keeps your global markup in place when market prices change.

## The problem

You set all prices to, say, 5% above market price on the computer's pricing tab. Then the market price of a few products changes at the start of a new day, and your selling prices stay put. Those products end up above or below your markup.

## Features

- Any product whose market price changes gets its price set again to your markup on top of the new price.
- Products you unlock with a new license get your markup straight away, so they never sell at cost.
- Prices you typed in by hand for single products are left alone.

## How it works

Apply a markup once from the pricing tab. The mod does the rest every day.

## Installation

Requires BepInEx 6 (IL2CPP). Copy `PriceMarkupKeeper.dll` into `BepInEx/plugins/`.
