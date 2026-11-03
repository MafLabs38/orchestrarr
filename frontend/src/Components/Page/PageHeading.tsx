import styles from './PageHeading.module.css';

export default function PageHeading({
  scope,
  title,
  description,
}: {
  scope?: string;
  title: string;
  description?: string;
}) {
  return (
    <div className={styles.heading}>
      {scope ? <div className={styles.scope}>{scope}</div> : null}
      <h1 className={styles.title}>{title}</h1>
      {description ? <p className={styles.description}>{description}</p> : null}
    </div>
  );
}
