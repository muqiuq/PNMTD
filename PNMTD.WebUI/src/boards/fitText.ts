/**
 * Text as large as its box allows (docs/board-messages-plan.md §5.2, §9.3): a binary search for the font size, in em of the parent's,
 * between `min` and `max`. The text fits when no word sticks out sideways and, unless `widthOnly`, it is no taller than the box.
 * `widthOnly` is for a box whose height follows its content: a page that scrolls, or an element of its own height. The text's size is
 * left at the result; `overflows` says even `min` didn't fit, for the caller to cut it.
 */
export function fitText(
  box: HTMLElement,
  text: HTMLElement,
  min: number,
  max: number,
  widthOnly = false,
): { size: number; overflows: boolean } {
  const fits = (size: number) => {
    text.style.fontSize = `${size}em`
    return text.scrollWidth <= text.clientWidth + 1 && (widthOnly || text.offsetHeight <= box.clientHeight + 1)
  }
  if (!fits(min)) return { size: min, overflows: true }
  let low = min
  let high = max
  if (fits(high)) return { size: high, overflows: false }
  // Fourteen halvings of 0.8 to 20 come within a thousandth of an em.
  for (let i = 0; i < 14 && high - low > 0.01; i++) {
    const mid = (low + high) / 2
    if (fits(mid)) low = mid
    else high = mid
  }
  const size = Math.floor(low * 1000) / 1000
  text.style.fontSize = `${size}em`
  return { size, overflows: false }
}
