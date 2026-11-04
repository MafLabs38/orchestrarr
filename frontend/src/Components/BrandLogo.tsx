interface BrandLogoProps {
  /** Height in pixels; the width follows the drawing's own proportions. */
  size: number;
}

/** The ant that conducts, a wrench in its hand: the maintainer's drawing. Decorative: the product name is always next to it, so it has no text of its own. */
export default function BrandLogo({ size }: Readonly<BrandLogoProps>) {
  return <img alt="" height={size} src="/logo.png" />;
}
